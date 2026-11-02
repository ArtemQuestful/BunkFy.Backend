namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingFlowTests
{
    [Fact]
    public async Task Requested_claim_consumes_a_newer_durable_withdrawal_and_redacts_staging()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        FakeRepository applications = new(application);
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new(
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId,
                claimId,
                claimVersion: 2,
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).Value);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            deferredWithdrawals: deferred);
        OrganizationEnrollmentClaimStaffOnboardingHandler handler = provider
            .GetRequiredService<OrganizationEnrollmentClaimStaffOnboardingHandler>();

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimChangedIntegrationEvent(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now.AddMinutes(1),
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId,
                claimId,
                application.SubjectId,
                OrganizationEnrollmentClaimChange.Requested,
                OrganizationEnrollmentClaimStatus.Pending,
                null,
                1),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Withdrawn, application.Status);
        Assert.Equal(2, application.ClaimVersion);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Empty(deferred.Items);
    }

    [Fact]
    public async Task Accepted_claim_event_records_its_version_before_provisioning()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeRepository applications = new(application);
        FakeStaffProvisioner staff = new();
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
            new FakeAccessControl());
        WorkspaceStaffAccessPlan acceptedPlan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(application.SourceId, CancellationToken.None))!;
        Assert.True(acceptedPlan.ObserveSourceExpired(
            WorkspaceStaffOnboardingTests.Now,
            WorkspaceStaffOnboardingTests.Now).IsSuccess);
        OrganizationEnrollmentClaimStaffOnboardingHandler handler = provider
            .GetRequiredService<OrganizationEnrollmentClaimStaffOnboardingHandler>();

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimChangedIntegrationEvent(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now.AddMinutes(1),
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                application.SubjectId,
                OrganizationEnrollmentClaimChange.Accepted,
                OrganizationEnrollmentClaimStatus.Accepted,
                Guid.NewGuid(),
                1),
            CancellationToken.None);

        Assert.Equal(1, application.ClaimVersion);
        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, acceptedPlan.Status);
        Assert.Equal(1, staff.CallCount);
        Assert.Equal(application.Id, staff.LastRequest?.OperationId);
    }

    [Fact]
    public async Task Rejected_claim_event_finalizes_a_source_expired_access_plan()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeRepository applications = new(application);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl());
        WorkspaceStaffAccessPlan plan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(application.SourceId, CancellationToken.None))!;
        Assert.True(plan.ObserveSourceExpired(
            WorkspaceStaffOnboardingTests.Now,
            WorkspaceStaffOnboardingTests.Now).IsSuccess);
        OrganizationEnrollmentClaimStaffOnboardingHandler handler = provider
            .GetRequiredService<OrganizationEnrollmentClaimStaffOnboardingHandler>();

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimChangedIntegrationEvent(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now.AddMinutes(1),
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                application.SubjectId,
                OrganizationEnrollmentClaimChange.Rejected,
                OrganizationEnrollmentClaimStatus.Rejected,
                null,
                1),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Rejected, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Owner_retry_cannot_provision_before_authoritative_acceptance()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeRepository applications = new(application);
        FakeStaffProvisioner staff = new();
        FakeAccessControl access = new();
        using ServiceProvider provider = CreateProvider(applications, staff, access);
        ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto> handler =
            provider.GetRequiredService<ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto>>();

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingErrors.StateConflict, result.Error);
        Assert.Equal(WorkspaceStaffOnboardingState.Submitted, application.Status);
        Assert.Equal(0, staff.CallCount);
        Assert.Equal(0, access.AssignmentCallCount);
    }

    [Theory]
    [InlineData("Staff.EmployeeNumberConflict")]
    [InlineData("Staff.StaffSuspended")]
    public async Task Staff_failure_never_grants_workspace_access(
        string errorCode)
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        FakeStaffProvisioner staff = new() { ErrorCode = errorCode };
        FakeAccessControl access = new();
        using ServiceProvider provider = CreateProvider(applications, staff, access);
        ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto> handler =
            provider.GetRequiredService<ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto>>();

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(errorCode, application.FailureCode);
        Assert.Null(application.StaffMemberId);
        Assert.Equal(1, staff.CallCount);
        Assert.Equal(0, access.AssignmentCallCount);
    }

    [Fact]
    public async Task Access_failure_retry_is_fail_closed_until_exact_owner_recovery()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        Guid staffMemberId = Guid.NewGuid();
        FakeStaffProvisioner staff = new() { StaffMemberId = staffMemberId };
        FakeAccessControl access = new() { FailAssignments = true };
        using ServiceProvider provider = CreateProvider(applications, staff, access);
        WorkspaceStaffAccessPlan retryPlan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(application.SourceId, CancellationToken.None))!;
        Assert.True(retryPlan.ObserveSourceExpired(
            WorkspaceStaffOnboardingTests.Now,
            WorkspaceStaffOnboardingTests.Now).IsSuccess);
        ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto> handler =
            provider.GetRequiredService<ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto>>();

        Result<WorkspaceStaffOnboardingDto> first = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, retryPlan.Status);
        access.FailAssignments = false;
        Result<WorkspaceStaffOnboardingDto> retried = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);

        Assert.True(first.IsFailure);
        Assert.True(retried.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptRequired,
            retried.Error);
        Assert.Equal(1, staff.CallCount);
        AssertProvisionerAssignment(Assert.Single(access.AssignmentCalls));
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(
            "Workspaces.AccessProvisioningFailed",
            application.FailureCode);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, retryPlan.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Old_retry_cannot_bypass_receipted_partial_recovery()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        Guid staffMemberId = Guid.NewGuid();
        FakeStaffProvisioner staff = new() { StaffMemberId = staffMemberId };
        FakeAccessControl access = new() { FailAssignments = true };
        FakeStaffStateVerifier staffState = new();
        FakeMembershipReader memberships = new();
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
            access,
            staffStateVerifiers: staffState,
            memberships: memberships);
        ICommandHandler<RetryWorkspaceStaffOnboardingCommand,
            WorkspaceStaffOnboardingDto> handler = provider.GetRequiredService<
                ICommandHandler<RetryWorkspaceStaffOnboardingCommand,
                    WorkspaceStaffOnboardingDto>>();

        Result<WorkspaceStaffOnboardingDto> failed = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);
        Assert.True(failed.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);

        access.FailAssignments = false;
        FakeStaffPropertyProvisioner staffProperties =
            Assert.IsType<FakeStaffPropertyProvisioner>(
                provider.GetRequiredService<
                    IStaffPropertyAssignmentProvisioner>());
        int accessWritesBeforeRecovery = access.AssignmentCallCount;
        int propertyWritesBeforeRecovery = staffProperties.CallCount;
        int membershipReadsBeforeRecovery = memberships.CallCount;

        Result<WorkspaceStaffOnboardingDto> blocked = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);

        Assert.True(blocked.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptRequired,
            blocked.Error);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
        Assert.Equal(1, staff.CallCount);
        Assert.Equal(accessWritesBeforeRecovery, access.AssignmentCallCount);
        Assert.Equal(propertyWritesBeforeRecovery, staffProperties.CallCount);
        Assert.Equal(0, staffState.CallCount);
        Assert.Equal(membershipReadsBeforeRecovery, memberships.CallCount);
    }

    [Fact]
    public async Task Exact_access_recovery_stays_failed_when_membership_is_not_active()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Guid staffMemberId = Guid.NewGuid();
        Assert.True(application.MarkStaffReady(
            staffMemberId,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Fail(
            "Workspaces.AccessProvisioningFailed",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3)).IsSuccess);
        FakeRepository applications = new(application);
        FakeStaffProvisioner staff = new() { StaffMemberId = staffMemberId };
        FakeAccessControl access = new();
        FakeStaffStateVerifier staffState = new();
        FakeMembershipReader memberships = new()
        {
            Status = OrganizationMembershipStatus.Suspended
        };
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
            access,
            staffStateVerifiers: staffState,
            memberships: memberships);
        AccessScope workspaceScope = WorkspaceAccessScopes.Create(
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"));
        AccessSubject subject = AccessSubject.User(application.SubjectId);
        access.Add(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            workspaceScope);
        Assert.IsType<FakeAccessProfiles>(
                provider.GetRequiredService<IScopedAccessProfileProvisioner>())
            .Assign(subject, workspaceScope);
        int accessWritesBeforeRetry = access.AssignmentCallCount;
        FakeStaffPropertyProvisioner staffProperties =
            Assert.IsType<FakeStaffPropertyProvisioner>(
                provider.GetRequiredService<
                    IStaffPropertyAssignmentProvisioner>());

        Result<WorkspaceStaffOnboardingDto> result = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptRequired,
            result.Error);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(accessWritesBeforeRetry, access.AssignmentCallCount);
        Assert.Equal(0, staffProperties.CallCount);
        Assert.Equal(0, staff.CallCount);
        Assert.Equal(0, staffState.CallCount);
        Assert.Equal(0, memberships.CallCount);
    }

    [Fact]
    public async Task Restriction_release_recovery_completes_the_last_application_and_finalizes_its_expired_source()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.BeginProvisioning(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        FakeRepository applications = new(application);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl());
        WorkspaceStaffAccessPlan plan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(application.SourceId, CancellationToken.None))!;
        Assert.True(plan.ObserveSourceExpired(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2),
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler handler =
            provider.GetRequiredService<
                WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>();

        await handler.HandleAsync(
            new WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent(
                Guid.NewGuid(),
                application.ScopeId,
                WorkspaceStaffOnboardingTests.Now.AddMinutes(3),
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                projectionRevision: 1,
                isRestricted: false),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Null(application.DisplayName);
    }

    private static void AssertProvisionerAssignment(
        (AccessSubject Subject, string RoleName, AccessScope Scope) call)
    {
        Assert.Equal(AccessSubject.System(WorkspaceAccessActors.Provisioner), call.Subject);
        Assert.Equal(WorkspaceAccessRoles.Provisioner, call.RoleName);
        Assert.Equal(
            WorkspaceAccessScopes.Create(WorkspaceStaffOnboardingTests.OrganizationId.ToString("D")),
            call.Scope);
    }

    [Fact]
    public async Task Submission_derives_workspace_authority_from_the_token_and_verified_identity()
    {
        Guid sourceId = Guid.NewGuid();
        FakeRepository applications = new();
        FakeJoinTokenInspector tokens = new(
            WorkspaceStaffOnboardingTests.OrganizationId,
            sourceId);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            tokens);
        IWorkspaceStaffOnboardingSubmitter submitter = provider
            .GetRequiredService<IWorkspaceStaffOnboardingSubmitter>();

        Result<WorkspaceStaffOnboardingDto> result = await submitter.SubmitAsync(
            new SubmitWorkspaceStaffOnboardingCommand(
                WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                "secret-token",
                WorkspaceStaffOnboardingTests.SubjectId,
                "Ada Operator",
                null,
                null,
                null,
                null,
                null,
                null),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(WorkspaceStaffOnboardingTests.OrganizationId, result.Value.OrganizationId);
        Assert.Equal(sourceId, result.Value.SourceId);
        Assert.Equal("verified@example.test", result.Value.VerifiedAccountEmail);
        Assert.Equal(WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            provider.GetRequiredService<IScopeContextAccessor>().ScopeId);
    }

    [Theory]
    [InlineData(OrganizationEnrollmentClaimStatus.Unknown)]
    [InlineData(OrganizationEnrollmentClaimStatus.Accepted)]
    [InlineData(OrganizationEnrollmentClaimStatus.Rejected)]
    [InlineData(OrganizationEnrollmentClaimStatus.Expired)]
    [InlineData(OrganizationEnrollmentClaimStatus.Withdrawn)]
    public async Task Terminal_or_unknown_enrollment_claim_fences_resubmission(
        OrganizationEnrollmentClaimStatus status)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "Profile A");
        FakeOrganizationEnrollmentClaimInspector claims = new(
            Claim(application, Guid.NewGuid(), status));
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId),
            claims: claims);

        Result<WorkspaceStaffOnboardingDto> result = await SubmitAsync(
            provider,
            WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
            application,
            "Profile B");

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .ProfileMutationAuthorityUnavailable,
            result.Error);
        Assert.Equal("Profile A", application.DisplayName);
        Assert.Equal(1, application.Version);
        Assert.Single(claims.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_exact_pending_enrollment_claim_allows_resubmission(
        bool hasPendingClaim)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "Profile A");
        FakeOrganizationEnrollmentClaimInspector claims = new(
            hasPendingClaim
                ? Claim(
                    application,
                    Guid.NewGuid(),
                    OrganizationEnrollmentClaimStatus.Pending)
                : null);
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId),
            claims: claims);

        Result<WorkspaceStaffOnboardingDto> result = await SubmitAsync(
            provider,
            WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
            application,
            "Profile B");

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Profile B", application.DisplayName);
        Assert.Equal(2, application.Version);
        Assert.Single(claims.Requests);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("source")]
    [InlineData("subject")]
    public async Task Mismatched_pending_enrollment_claim_fences_resubmission(
        string coordinate)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "Profile A");
        OrganizationEnrollmentClaimDto claim = Claim(
            application,
            Guid.NewGuid(),
            OrganizationEnrollmentClaimStatus.Pending);
        claim = coordinate switch
        {
            "organization" => claim with { OrganizationId = Guid.NewGuid() },
            "source" => claim with { EnrollmentLinkId = Guid.NewGuid() },
            _ => claim with { SubjectId = Guid.NewGuid().ToString("D") }
        };
        FakeOrganizationEnrollmentClaimInspector claims = new(claim);
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId),
            claims: claims);

        Result<WorkspaceStaffOnboardingDto> result = await SubmitAsync(
            provider,
            WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
            application,
            "Profile B");

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .ProfileMutationAuthorityUnavailable,
            result.Error);
        Assert.Equal("Profile A", application.DisplayName);
    }

    [Fact]
    public async Task Invitation_resubmission_does_not_query_enrollment_claims()
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            "Profile A");
        FakeOrganizationEnrollmentClaimInspector claims = new(
            Claim(
                application,
                Guid.NewGuid(),
                OrganizationEnrollmentClaimStatus.Accepted));
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId),
            claims: claims);

        Result<WorkspaceStaffOnboardingDto> result = await SubmitAsync(
            provider,
            WorkspaceStaffOnboardingSourceKind.Invitation,
            application,
            "Profile B");

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Profile B", application.DisplayName);
        Assert.Empty(claims.Requests);
    }

    [Fact]
    public async Task Submission_rejects_a_member_without_current_auth_admission()
    {
        Guid sourceId = Guid.NewGuid();
        FakeRepository applications = new();
        FakeAdmissionReader admissions = new() { Admission = null };
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                sourceId),
            admissions);
        IWorkspaceStaffOnboardingSubmitter submitter = provider
            .GetRequiredService<IWorkspaceStaffOnboardingSubmitter>();

        Result<WorkspaceStaffOnboardingDto> result = await submitter.SubmitAsync(
            new SubmitWorkspaceStaffOnboardingCommand(
                WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                "secret-token",
                WorkspaceStaffOnboardingTests.SubjectId,
                "Ada Operator",
                null,
                null,
                null,
                null,
                null,
                null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired,
            result.Error);
        Assert.Empty(applications.Applications);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public async Task Terminal_source_replay_returns_the_existing_application_without_reopening_it(
        WorkspaceStaffOnboardingSourceKind sourceKind)
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffOnboardingSource domainSource = sourceKind ==
            WorkspaceStaffOnboardingSourceKind.Invitation
                ? WorkspaceStaffOnboardingSource.Invitation
                : WorkspaceStaffOnboardingSource.EnrollmentLink;
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            domainSource,
            sourceId,
            WorkspaceStaffOnboardingTests.SubjectId,
            "verified@example.test",
            "Ada Operator",
            null,
            null,
            null,
            null,
            null,
            null,
            WorkspaceStaffOnboardingTests.Now).Value;
        Result accepted = domainSource == WorkspaceStaffOnboardingSource.Invitation
            ? application.ObserveInvitationAccepted(
                WorkspaceStaffOnboardingTests.Now.AddMinutes(1))
            : application.ObserveClaimAccepted(
                Guid.NewGuid(),
                1,
                WorkspaceStaffOnboardingTests.Now.AddMinutes(1));
        Assert.True(accepted.IsSuccess, accepted.Error.Code);
        Assert.True(application.MarkStaffReady(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Complete(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3)).IsSuccess);
        long completedVersion = application.Version;

        FakeRepository applications = new(application);
        FakeJoinTokenInspector tokens = new(
            WorkspaceStaffOnboardingTests.OrganizationId,
            sourceId,
            OrganizationInvitationStatus.Accepted,
            OrganizationEnrollmentLinkStatus.Disabled);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            tokens);
        WorkspaceStaffAccessPlan plan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(sourceId, CancellationToken.None))!;
        Assert.True(plan.Supersede(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(4)).IsSuccess);

        Result<WorkspaceStaffOnboardingDto> replayed = await provider
            .GetRequiredService<IWorkspaceStaffOnboardingSubmitter>()
            .SubmitAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    sourceKind,
                    "secret-token",
                    WorkspaceStaffOnboardingTests.SubjectId,
                    "Changed after completion",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                CancellationToken.None);

        Assert.True(replayed.IsSuccess, replayed.Error.Code);
        Assert.Equal(application.Id, replayed.Value.ApplicationId);
        Assert.Equal(WorkspaceStaffOnboardingStatus.Completed, replayed.Value.Status);
        Assert.Equal(completedVersion, application.Version);
        Assert.Null(application.DisplayName);
        Assert.Single(applications.Applications);
        Assert.Equal(WorkspaceStaffAccessPlanState.Superseded, plan.Status);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public async Task Terminal_source_cannot_create_a_new_application(
        WorkspaceStaffOnboardingSourceKind sourceKind)
    {
        Guid sourceId = Guid.NewGuid();
        FakeRepository applications = new();
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                sourceId,
                OrganizationInvitationStatus.Accepted,
                OrganizationEnrollmentLinkStatus.Disabled));

        Result<WorkspaceStaffOnboardingDto> result = await provider
            .GetRequiredService<IWorkspaceStaffOnboardingSubmitter>()
            .SubmitAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    sourceKind,
                    "secret-token",
                    WorkspaceStaffOnboardingTests.SubjectId,
                    "Ada Operator",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.JoinTokenInvalid,
            result.Error);
        Assert.Empty(applications.Applications);
    }

    [Fact]
    public async Task Terminal_source_replay_still_requires_current_auth_admission()
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            WorkspaceStaffOnboardingSource.Invitation,
            sourceId,
            WorkspaceStaffOnboardingTests.SubjectId,
            "verified@example.test",
            "Ada Operator",
            null,
            null,
            null,
            null,
            null,
            null,
            WorkspaceStaffOnboardingTests.Now).Value;
        FakeAdmissionReader admissions = new() { Admission = null };
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                sourceId,
                OrganizationInvitationStatus.Accepted),
            admissions);

        Result<WorkspaceStaffOnboardingDto> result = await provider
            .GetRequiredService<IWorkspaceStaffOnboardingSubmitter>()
            .SubmitAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    WorkspaceStaffOnboardingSourceKind.Invitation,
                    "secret-token",
                    WorkspaceStaffOnboardingTests.SubjectId,
                    "Ada Operator",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired,
            result.Error);
    }

    [Fact]
    public async Task Admission_requires_the_exact_source_subject_and_current_verified_email()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeAdmissionReader admissions = new();
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            admissions: admissions);
        IOrganizationJoinAdmissionPolicy policy = provider
            .GetServices<IOrganizationJoinAdmissionPolicy>()
            .Single();

        OrganizationJoinAdmissionContext valid = new(
            OrganizationJoinAdmissionOperation.ClaimEnrollment,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SourceId,
            null,
            application.SubjectId,
            application.SubjectId,
            OrganizationEnrollmentApprovalMode.RequiresApproval);

        Assert.Equal(
            OrganizationJoinAdmissionDecision.Allowed,
            await policy.EvaluateAsync(valid));
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(valid with { SourceId = Guid.NewGuid() }));
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(valid with
            {
                ApplicantSubjectId = Guid.NewGuid().ToString("D")
            }));

        admissions.Admission = new AuthMemberAdmission("changed@example.test");
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(valid));

        admissions.Admission = null;
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(valid));
    }

    [Fact]
    public async Task Approval_requires_the_claim_bound_to_the_staff_application()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(
            claimId,
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl());
        IOrganizationJoinAdmissionPolicy policy = provider
            .GetServices<IOrganizationJoinAdmissionPolicy>()
            .Single();

        OrganizationJoinAdmissionContext approval = new(
            OrganizationJoinAdmissionOperation.ApproveEnrollment,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SourceId,
            claimId,
            application.SubjectId,
            Guid.NewGuid().ToString("D"),
            OrganizationEnrollmentApprovalMode.RequiresApproval);

        Assert.Equal(
            OrganizationJoinAdmissionDecision.Allowed,
            await policy.EvaluateAsync(approval));
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(approval with { ClaimId = Guid.NewGuid() }));
        Assert.Equal(
            OrganizationJoinAdmissionDecision.Denied,
            await policy.EvaluateAsync(approval with { ClaimId = null }));
    }

    [Fact]
    public async Task Admission_is_denied_while_workspace_termination_fence_is_active()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            terminationFence: new WorkspaceTerminationFenceSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceTerminationFenceState.Frozen,
                Version: 1));
        IOrganizationJoinAdmissionPolicy policy = provider
            .GetServices<IOrganizationJoinAdmissionPolicy>()
            .Single();

        OrganizationJoinAdmissionDecision decision = await policy.EvaluateAsync(new(
            OrganizationJoinAdmissionOperation.ClaimEnrollment,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SourceId,
            null,
            application.SubjectId,
            application.SubjectId,
            OrganizationEnrollmentApprovalMode.RequiresApproval));

        Assert.Equal(OrganizationJoinAdmissionDecision.Denied, decision);
    }

    [Fact]
    public async Task Admission_reports_unavailable_when_workspace_state_is_not_authoritative()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            terminationFence: new WorkspaceTerminationFenceSnapshot(
                Guid.Empty,
                Guid.NewGuid(),
                WorkspaceTerminationFenceState.Frozen,
                Version: 1));
        IOrganizationJoinAdmissionPolicy policy = provider
            .GetServices<IOrganizationJoinAdmissionPolicy>()
            .Single();

        OrganizationJoinAdmissionDecision decision = await policy.EvaluateAsync(new(
            OrganizationJoinAdmissionOperation.ClaimEnrollment,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SourceId,
            null,
            application.SubjectId,
            application.SubjectId,
            OrganizationEnrollmentApprovalMode.RequiresApproval));

        Assert.Equal(OrganizationJoinAdmissionDecision.Unavailable, decision);
    }

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid applicationId,
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        string displayName) =>
        WorkspaceStaffOnboarding.Create(
            applicationId,
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            sourceKind,
            sourceId,
            WorkspaceStaffOnboardingTests.SubjectId,
            "verified@example.test",
            displayName,
            "Ada Lovelace",
            "ada@workspace.test",
            "+1 555 0100",
            "EMP-100",
            "Manager",
            "Operations",
            WorkspaceStaffOnboardingTests.Now).Value;

    private static OrganizationEnrollmentClaimDto Claim(
        WorkspaceStaffOnboarding application,
        Guid claimId,
        OrganizationEnrollmentClaimStatus status) => new(
        claimId,
        application.SourceId,
        WorkspaceStaffOnboardingTests.OrganizationId,
        application.SubjectId,
        status,
        status == OrganizationEnrollmentClaimStatus.Accepted
            ? Guid.NewGuid()
            : null,
        Version: 2,
        WorkspaceStaffOnboardingTests.Now,
        WorkspaceStaffOnboardingTests.Now.AddMinutes(1))
        {
            DecisionExpiresAtUtc =
            status == OrganizationEnrollmentClaimStatus.Pending
                ? WorkspaceStaffOnboardingTests.Now.AddMinutes(5)
                : null
        };

    private static Task<Result<WorkspaceStaffOnboardingDto>> SubmitAsync(
        ServiceProvider provider,
        WorkspaceStaffOnboardingSourceKind sourceKind,
        WorkspaceStaffOnboarding application,
        string displayName) =>
        provider.GetRequiredService<IWorkspaceStaffOnboardingSubmitter>()
            .SubmitAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    sourceKind,
                    "secret-token",
                    application.SubjectId,
                    displayName,
                    application.LegalName,
                    application.WorkEmail,
                    application.WorkPhone,
                    application.EmployeeNumber,
                    application.JobTitle,
                    application.Department),
                CancellationToken.None);

    private static ServiceProvider CreateProvider(
        FakeRepository applications,
        FakeStaffProvisioner staff,
        FakeAccessControl access,
        FakeJoinTokenInspector? tokens = null,
        FakeAdmissionReader? admissions = null,
        WorkspaceTerminationFenceSnapshot? terminationFence = null,
        FakeStaffStateVerifier? staffStateVerifiers = null,
        FakeMembershipReader? memberships = null,
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository?
            deferredWithdrawals = null,
        FakeOrganizationEnrollmentClaimInspector? claims = null)
    {
        HostApplicationBuilder builder = new(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.AddCqrsInfrastructure();
        IServiceCollection services = builder.Services;
        FakeAccessProfiles profiles = new();
        FakeJoinTokenInspector tokenInspector = tokens ?? new FakeJoinTokenInspector(
            WorkspaceStaffOnboardingTests.OrganizationId,
            Guid.NewGuid());
        WorkspaceStaffAccessPlan[] plans = applications.Applications
            .Select(application => CreateActivePlan(
                application.SourceKind,
                application.SourceId,
                profiles.ProfileId))
            .Append(CreateActivePlan(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                tokenInspector.SourceId,
                profiles.ProfileId))
            .GroupBy(plan => plan.Id)
            .Select(group => group.First())
            .ToArray();
        services.AddLogging();
        services.AddSingleton<IWorkspaceStaffOnboardingRepository>(applications);
        services.AddSingleton<
            IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository>(
            new FakeRestrictionProjectionRepository(
                applications.Applications));
        services.AddSingleton<IWorkspaceStaffOnboardingOperationLock>(
            new FakeOperationLock());
        services.AddSingleton<IWorkspaceStaffAccessPlanRepository>(new FakeAccessPlanRepository(plans));
        services.AddSingleton<IWorkspaceStaffDeferredClaimWithdrawalRepository>(
            deferredWithdrawals ??
                new FakeWorkspaceStaffDeferredClaimWithdrawalRepository());
        services.AddSingleton<IStaffOnboardingProvisioner>(staff);
        services.AddSingleton<IStaffPropertyAssignmentProvisioner>(
            new FakeStaffPropertyProvisioner());
        services.AddSingleton<IStaffOnboardingStateVerifier>(
            staffStateVerifiers ?? new FakeStaffStateVerifier());
        services.AddSingleton<IOrganizationMembershipReader>(
            memberships ?? new FakeMembershipReader());
        services.AddSingleton<IOrganizationScopeLifecycle>(
            new FakeOrganizationScopeLifecycle());
        services.AddSingleton<IAccessControlRoleProvisioner>(access);
        services.AddSingleton<IAccessProfileProvisioner>(profiles);
        services.AddSingleton<IScopedAccessProfileProvisioner>(profiles);
        services.AddSingleton<IScopedAccessProfileManager>(profiles);
        services.AddSingleton<IAccessAuthorizationService>(new AllowAllAuthorizationService());
        services.AddSingleton<IWorkspacePropertyProjectionRepository>(new FakePropertyProjectionRepository());
        services.AddSingleton<IOrganizationJoinTokenInspector>(tokenInspector);
        services.AddSingleton<IOrganizationEnrollmentClaimInspector>(
            claims ?? new FakeOrganizationEnrollmentClaimInspector());
        services.AddSingleton<IAuthMemberAdmissionReader>(
            admissions ?? new FakeAdmissionReader());
        services.AddSingleton<IWorkspaceTerminationFenceReader>(
            new FakeTerminationFenceReader(terminationFence));
        services.AddSingleton<IScopeContextAccessor>(new FakeScopeContext(
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D")));
        services.AddSingleton<IScopeContext>(provider => provider.GetRequiredService<IScopeContextAccessor>());
        services.AddSingleton<ISystemClock>(new FakeClock());
        services.AddSingleton<IIdGenerator>(new FakeIdGenerator());
        services.AddSingleton<IUnitOfWork>(new TestUnitOfWork());
        services.AddWorkspacesApplication(new ConfigurationBuilder().Build(), "global");
        return services.BuildServiceProvider();
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public string ModuleName => WorkspacesModuleMetadata.Name;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static WorkspaceStaffAccessPlan CreateActivePlan(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        Guid profileId)
    {
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            sourceKind,
            profileId,
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            [],
            WorkspaceStaffOnboardingTests.SubjectId,
            WorkspaceStaffOnboardingTests.Now).Value;
        Assert.True(plan.Activate(WorkspaceStaffOnboardingTests.Now.AddSeconds(1)).IsSuccess);
        return plan;
    }

    private sealed class FakeRepository(params WorkspaceStaffOnboarding[] seed)
        : IWorkspaceStaffOnboardingRepository
    {
        private readonly List<WorkspaceStaffOnboarding> applications = [.. seed];

        public IReadOnlyList<WorkspaceStaffOnboarding> Applications => this.applications;

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            WorkspaceStaffOnboarding? application = this.applications
                .SingleOrDefault(item => item.Id == applicationId);
            return Task.FromResult(application is null
                ? null
                : new WorkspaceStaffOnboardingCoordinate(
                    application.Id,
                    application.SourceKind,
                    application.SourceId));
        }

        public Task<WorkspaceStaffOnboarding?> GetAsync(Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult(this.applications.SingleOrDefault(item => item.Id == applicationId));

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(applicationId, cancellationToken);

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            this.GetBySourceAndSubjectForLifecycleAsync(
                sourceKind,
                sourceId,
                subjectId,
                cancellationToken);

        public Task<WorkspaceStaffOnboarding?>
            GetBySourceAndSubjectForLifecycleAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.applications.SingleOrDefault(item =>
                item.SourceKind == sourceKind &&
                item.SourceId == sourceId &&
                string.Equals(item.SubjectId, subjectId, StringComparison.Ordinal)));
        }

        public async Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            (await this.GetBySourceAndSubjectForLifecycleAsync(
                sourceKind,
                sourceId,
                subjectId,
                cancellationToken))?.Id;

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(Guid claimId, CancellationToken cancellationToken) =>
            Task.FromResult(this.applications.SingleOrDefault(item => item.ClaimId == claimId));

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>> ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkspaceStaffOnboarding>>(
                this.applications.Where(item => item.SourceKind == sourceKind && item.SourceId == sourceId).ToArray());

        public Task<WorkspaceStaffOnboardingListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) => Task.FromResult(
                new WorkspaceStaffOnboardingListResponse(
                    [],
                    page.Page,
                    page.PageSize,
                    HasMore: false));

        public Task ReloadAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddAsync(WorkspaceStaffOnboarding application, CancellationToken cancellationToken)
        {
            this.applications.Add(application);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccessPlanRepository(params WorkspaceStaffAccessPlan[] seed)
        : IWorkspaceStaffAccessPlanRepository
    {
        private readonly List<WorkspaceStaffAccessPlan> plans = [.. seed];

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.plans.SingleOrDefault(plan => plan.Id == sourceId));

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>> GetManyAsync(
            IReadOnlyCollection<Guid> sourceIds,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<
                Guid,
                WorkspaceStaffAccessPlan>>(this.plans
                    .Where(plan => sourceIds.Contains(plan.Id))
                    .ToDictionary(plan => plan.Id));

        public Task AddAsync(
            WorkspaceStaffAccessPlan plan,
            CancellationToken cancellationToken)
        {
            this.plans.Add(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestrictionProjectionRepository(
        IEnumerable<WorkspaceStaffOnboarding> applications)
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        private readonly Dictionary<Guid,
            WorkspaceStaffOnboardingProcessingRestrictionProjection>
            projections = applications.ToDictionary(
                application => application.Id,
                application =>
                    WorkspaceStaffOnboardingProcessingRestrictionProjection
                        .Create(
                            application.ScopeId,
                            application.Id,
                            WorkspaceStaffOnboardingProcessingRestrictionContract
                                .CurrentVersion,
                            application.CreatedAtUtc).Value);

        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.projections.GetValueOrDefault(applicationId));

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection projection,
            CancellationToken cancellationToken)
        {
            this.projections.Add(projection.ApplicationId, projection);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOperationLock
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeStaffProvisioner : IStaffOnboardingProvisioner
    {
        public Guid? StaffMemberId { get; init; } = Guid.NewGuid();
        public string? ErrorCode { get; init; }
        public int CallCount { get; private set; }
        public StaffOnboardingProvisioningRequest? LastRequest { get; private set; }

        public Task<StaffOnboardingProvisioningResult> ProvisionAsync(
            StaffOnboardingProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            this.LastRequest = request;
            return Task.FromResult(this.ErrorCode is null
                ? new StaffOnboardingProvisioningResult(true, this.StaffMemberId, null)
                : new StaffOnboardingProvisioningResult(false, null, this.ErrorCode));
        }
    }

    private sealed class FakeStaffPropertyProvisioner : IStaffPropertyAssignmentProvisioner
    {
        public int CallCount { get; private set; }

        public Task<StaffPropertyAssignmentProvisioningResult> ReconcileAsync(
            StaffPropertyAssignmentProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(
                new StaffPropertyAssignmentProvisioningResult(
                    true,
                    request.PropertyIds.ToArray(),
                    null));
        }
    }

    private sealed class FakeStaffStateVerifier : IStaffOnboardingStateVerifier
    {
        public StaffOnboardingStateVerificationResult Result { get; set; } =
            new(
                StaffOnboardingStateVerificationStatus.Equivalent,
                StaffOnboardingStateVerificationReason.Equivalent,
                1,
                1,
                1,
                1,
                0,
                new string('a', 64),
                new string('b', 64),
                WorkspaceStaffOnboardingTests.Now);
        public int CallCount { get; private set; }
        public StaffOnboardingStateVerificationRequest? LastRequest
        {
            get;
            private set;
        }

        public Task<StaffOnboardingStateVerificationResult> VerifyAsync(
            StaffOnboardingStateVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            this.LastRequest = request;
            return Task.FromResult(this.Result);
        }
    }

    private sealed class FakeMembershipReader : IOrganizationMembershipReader
    {
        private readonly Guid membershipId = Guid.NewGuid();
        public OrganizationMembershipStatus Status { get; set; } =
            OrganizationMembershipStatus.Active;
        public int CallCount { get; private set; }

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult<OrganizationMembershipSnapshotDto?>(new(
                organizationId,
                OrganizationStatus.Active,
                new OrganizationMembershipDto(
                    this.membershipId,
                    organizationId,
                    subjectId,
                    OrganizationMembershipRole.Member,
                    this.Status,
                    1,
                    WorkspaceStaffOnboardingTests.Now,
                    WorkspaceStaffOnboardingTests.Now)));
        }
    }

    private sealed class FakeOrganizationScopeLifecycle
        : IOrganizationScopeLifecycle
    {
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken) => Task.FromResult(
            new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 1));

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAccessControl : IAccessControlRoleProvisioner
    {
        private readonly HashSet<(AccessSubject Subject, string RoleName,
            AccessScope Scope)> assignments = [];
        public bool FailAssignments { get; set; }
        public List<(AccessSubject Subject, string RoleName, AccessScope Scope)> AssignmentCalls { get; } = [];
        public int AssignmentCallCount => this.AssignmentCalls.Count;

        public Task EnsureRoleAsync(AccessControlRoleDefinition role, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.AssignmentCalls.Add((subject, roleName, scope));
            if (this.FailAssignments)
            {
                return Task.FromException(
                    new InvalidOperationException("Access unavailable."));
            }

            this.assignments.Add((subject, roleName, scope));
            return Task.CompletedTask;
        }

        public void Add(
            AccessSubject subject,
            string roleName,
            AccessScope scope) =>
            this.assignments.Add((subject, roleName, scope));

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            bool removed = this.assignments.Remove((subject, roleName, scope));
            return Task.FromResult(removed
                ? AccessControlAssignmentRemovalOutcome.Removed
                : AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult(
                this.assignments.Contains((subject, roleName, scope)));

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new AccessControlPage<AccessControlRoleAssignment>([], page, pageSize, false));
    }

    private sealed class FakeAccessProfiles :
        IAccessProfileProvisioner,
        IScopedAccessProfileProvisioner,
        IScopedAccessProfileManager
    {
        private readonly Guid frontDeskId = Guid.NewGuid();
        private readonly List<ScopedAccessProfileAssignment> assignments = [];

        public Guid ProfileId => this.frontDeskId;

        public void Assign(AccessSubject subject, AccessScope scope)
        {
            ArgumentNullException.ThrowIfNull(subject);
            this.assignments.RemoveAll(assignment =>
                assignment.AssignmentScope == scope);
            this.assignments.Add(new ScopedAccessProfileAssignment(
                this.CreateProfile(WorkspaceAccessScopes.Create(
                    WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"))),
                scope));
        }

        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new AccessProfileDto(
                this.frontDeskId,
                ownerScope.Value,
                definition.Key,
                definition.DisplayName,
                definition.Description ?? string.Empty,
                AccessProfileStatus.Active,
                1,
                definition.Permissions.ToArray(),
                0,
                WorkspaceStaffOnboardingTests.Now,
                WorkspaceStaffOnboardingTests.Now));

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) => Task.FromResult<AccessProfileDto?>(
                new AccessProfileDto(
                    this.frontDeskId,
                    ownerScope.Value,
                    WorkspaceAccessProfileSeeds.FrontDeskKey,
                    "Front desk",
                    "Front desk operations.",
                    AccessProfileStatus.Active,
                    1,
                    WorkspaceAccessProfileSeeds.FrontDesk.Permissions.ToArray(),
                    0,
                    WorkspaceStaffOnboardingTests.Now,
                    WorkspaceStaffOnboardingTests.Now));

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new AccessProfileAssignmentSet(subject, ownerScope, []));

        public Task<AccessProfileAssignmentReconciliation> ReconcileSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            IReadOnlyCollection<Guid> profileIds,
            AccessSubject actor,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new AccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                profileIds.ToArray(),
                profileIds.Count,
                0));

        public Task<ScopedAccessProfileAssignmentSet> GetSubjectScopedAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ScopedAccessProfileAssignmentSet(
                subject,
                ownerScope,
                this.assignments.ToArray()));

        public Task<ScopedAccessProfileAssignmentReconciliation> ReconcileSubjectScopedAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            int removed = this.assignments.Count;
            this.assignments.Clear();
            foreach (AccessProfileAssignmentTarget target in targets)
            {
                this.assignments.Add(new ScopedAccessProfileAssignment(
                    this.CreateProfile(ownerScope),
                    target.AssignmentScope));
            }

            return Task.FromResult(
                new ScopedAccessProfileAssignmentReconciliation(
                    subject,
                    ownerScope,
                    targets.ToArray(),
                    targets.Count,
                    removed));
        }

        async Task<Result<ScopedAccessProfileAssignmentSet>>
            IScopedAccessProfileManager.GetSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                AccessSubject actor,
                CancellationToken cancellationToken) =>
            Result.Success(await this.GetSubjectScopedAssignmentsAsync(
                    subject,
                    ownerScope,
                    cancellationToken)
                .ConfigureAwait(false));

        async Task<Result<ScopedAccessProfileAssignmentReconciliation>>
            IScopedAccessProfileManager.ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken) =>
            Result.Success(await this.ReconcileSubjectScopedAssignmentsAsync(
                    subject,
                    ownerScope,
                    targets,
                    actor,
                    cancellationToken)
                .ConfigureAwait(false));

        private AccessProfileDto CreateProfile(AccessScope ownerScope) => new(
            this.frontDeskId,
            ownerScope.Value,
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            "Front desk",
            "Front desk operations.",
            AccessProfileStatus.Active,
            1,
            WorkspaceAccessProfileSeeds.FrontDesk.Permissions.ToArray(),
            this.assignments.Count,
            WorkspaceStaffOnboardingTests.Now,
            WorkspaceStaffOnboardingTests.Now);
    }

    private sealed class FakeJoinTokenInspector(
        Guid organizationId,
        Guid sourceId,
        OrganizationInvitationStatus invitationStatus =
            OrganizationInvitationStatus.Pending,
        OrganizationEnrollmentLinkStatus enrollmentStatus =
            OrganizationEnrollmentLinkStatus.Active)
        : IOrganizationJoinTokenInspector
    {
        public Guid SourceId => sourceId;

        public Task<OrganizationJoinTokenInspection<OrganizationInvitationPreviewDto>> InspectInvitationAsync(
            string token,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new OrganizationJoinTokenInspection<OrganizationInvitationPreviewDto>(
                    new OrganizationInvitationPreviewDto(
                        sourceId,
                        organizationId,
                        "Workspace",
                        "workspace",
                        false,
                        WorkspaceStaffOnboardingTests.Now.AddDays(1),
                        invitationStatus),
                    null));

        public Task<OrganizationJoinTokenInspection<OrganizationEnrollmentPreviewDto>> InspectEnrollmentAsync(
            string token,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new OrganizationJoinTokenInspection<OrganizationEnrollmentPreviewDto>(
                    new OrganizationEnrollmentPreviewDto(
                        sourceId,
                        organizationId,
                        "Workspace",
                        "workspace",
                        WorkspaceStaffOnboardingTests.Now.AddDays(1),
                        5,
                        OrganizationEnrollmentApprovalMode.RequiresApproval,
                        enrollmentStatus),
                    null));
    }

    private sealed class AllowAllAuthorizationService : IAccessAuthorizationService
    {
        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccessDecision.Allowed());
    }

    private sealed class FakePropertyProjectionRepository : IWorkspacePropertyProjectionRepository
    {
        public Task<bool> AreAllActiveAsync(
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ApplyAsync(
            WorkspacePropertyProjectionWriteModel property,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAdmissionReader : IAuthMemberAdmissionReader
    {
        public AuthMemberAdmission? Admission { get; set; } =
            new("verified@example.test");

        public ValueTask<AuthMemberAdmission?> FindActiveAsync(
            string scopeId,
            Guid memberId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(this.Admission);
    }

    private sealed class FakeTerminationFenceReader(
        WorkspaceTerminationFenceSnapshot? fence)
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(fence);
    }

    private sealed class FakeScopeContext(string? initialScopeId) : IScopeContextAccessor
    {
        public bool IsEnabled => !string.IsNullOrWhiteSpace(this.ScopeId);
        public string? ScopeId { get; private set; } = initialScopeId;
        public void SetScope(string scopeId) => this.ScopeId = scopeId;
        public void ClearScope() => this.ScopeId = null;
    }

    private sealed class FakeClock : ISystemClock
    {
        private int ticks;
        public DateTimeOffset UtcNow => WorkspaceStaffOnboardingTests.Now.AddSeconds(this.ticks++);
    }

    private sealed class FakeIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}
