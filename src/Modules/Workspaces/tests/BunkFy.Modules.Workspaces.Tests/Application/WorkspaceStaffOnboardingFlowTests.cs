namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Events;
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
    public async Task Continuation_cannot_reopen_failed_access_without_receipted_owner_recovery()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        Assert.True(application.MarkStaffReady(
            staffMemberId,
            resolutionEventId,
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Fail(
            "Workspaces.AccessProvisioningFailed",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3)).IsSuccess);
        long failedVersion = application.Version;
        FakeAccessControl access = new();
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId));
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner { StaffMemberId = staffMemberId },
            access,
            anchorOutcomes: outcomes);

        Result result = await provider
            .GetRequiredService<WorkspaceStaffOnboardingProcessor>()
            .ProcessIdentityAnchorContinuationAsync(
                application,
                staffMemberId,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptRequired,
            result.Error);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(failedVersion, application.Version);
        Assert.Equal("Workspaces.AccessProvisioningFailed", application.FailureCode);
        Assert.Equal(0, access.AssignmentCallCount);
        Assert.Equal(0, Assert.IsType<FakeStaffPropertyProvisioner>(provider
            .GetRequiredService<IStaffPropertyAssignmentProvisioner>()).CallCount);
    }

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
        List<string> operationCalls = [];
        FakeOperationLock operationLock = new(operationCalls);
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
            new FakeAccessControl(),
            operationLock: operationLock);
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
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, acceptedPlan.Status);
        Assert.Equal(1, staff.CallCount);
        Assert.Equal(application.Id, staff.LastRequest?.OperationId);
        Assert.Null(application.DisplayName);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            continuation = Assert.Single(application.DomainEvents.OfType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>());
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuationFact = new(
                continuation.EventId,
                continuation.ScopeId,
                continuation.OccurredAtUtc,
                continuation.ApplicationId,
                continuation.StaffMemberId);
        WorkspaceStaffOnboardingIdentityAnchorContinuationHandler continuationHandler =
            provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>();
        operationCalls.Clear();

        await continuationHandler.HandleAsync(
            continuationFact,
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, acceptedPlan.Status);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
        long completedVersion = application.Version;

        await continuationHandler.HandleAsync(
            continuationFact,
            CancellationToken.None);

        Assert.Equal(completedVersion, application.Version);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
        Assert.Equal(
            ["source-write", "application", "source-write", "application"],
            operationCalls);
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
    public async Task Cancelled_access_attempt_retries_without_duplicate_staff_and_then_redacts_application()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimAccepted(
            claimId,
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        Guid staffMemberId = Guid.NewGuid();
        FakeStaffProvisioner staff = new() { StaffMemberId = staffMemberId };
        FakeAccessControl access = new() { CancelAssignments = true };
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
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingStatus.StaffReady,
            first.Value.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, retryPlan.Status);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);
        WorkspaceStaffOnboardingIdentityAnchorContinuationHandler
            continuationHandler = provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            continuationHandler.HandleAsync(
                continuation,
                CancellationToken.None));

        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        WorkspaceStaffOnboarding rolledBack = CreateRolledBackStaffReady(
            application,
            claimId,
            staffMemberId);
        applications.Replace(rolledBack);
        application = rolledBack;
        access.CancelAssignments = false;

        await continuationHandler.HandleAsync(
            continuation,
            CancellationToken.None);

        Assert.Equal(1, staff.CallCount);
        Assert.Collection(
            access.AssignmentCalls,
            AssertProvisionerAssignment,
            AssertProvisionerAssignment,
            call =>
            {
                Assert.Equal(AccessSubject.User(WorkspaceStaffOnboardingTests.SubjectId), call.Subject);
                Assert.Equal(WorkspaceAccessRoles.MembershipMarker, call.RoleName);
                Assert.Equal(
                    WorkspaceAccessScopes.Create(WorkspaceStaffOnboardingTests.OrganizationId.ToString("D")),
                    call.Scope);
            });
        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, retryPlan.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
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
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);
        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, retryPlan.Status);
        access.FailAssignments = false;
        Result<WorkspaceStaffOnboardingDto> retried = await handler.HandleAsync(
            new RetryWorkspaceStaffOnboardingCommand(application.Id),
            CancellationToken.None);

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
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Legacy_and_continuation_replays_cannot_bypass_receipted_partial_recovery()
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
        Assert.True(failed.IsSuccess, failed.Error.Code);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);
        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);
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

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);

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
            Guid.NewGuid(),
            Guid.NewGuid(),
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
        FakeStaffProvisioner staff = new();
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
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

        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Equal(1, staff.CallCount);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Restriction_release_is_a_durable_trigger_after_continuation_delivery_exhausts()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        FakeStaffProvisioner staff = new();
        FakeRestrictionProjectionRepository restrictions = new(
            applications.Applications);
        using ServiceProvider provider = CreateProvider(
            applications,
            staff,
            new FakeAccessControl(),
            restrictionProjections: restrictions);
        WorkspaceStaffAccessPlan plan = (await provider
            .GetRequiredService<IWorkspaceStaffAccessPlanRepository>()
            .GetAsync(application.SourceId, CancellationToken.None))!;
        Assert.True(plan.ObserveSourceExpired(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2),
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);

        Result<WorkspaceStaffOnboardingDto> first = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            restrictions.Get(application.Id);
        Assert.True(projection.Apply(
            expectedRevision: 0,
            WorkspaceStaffOnboardingProcessingRestrictionContract.CurrentVersion,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3)).IsSuccess);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider
            .GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None));

        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Null(application.IdentityAnchorResolutionEventId);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.True(projection.Release(
            expectedRevision: 1,
            WorkspaceStaffOnboardingProcessingRestrictionContract.CurrentVersion,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(4)).IsSuccess);

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>()
            .HandleAsync(
                new WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent(
                    Guid.NewGuid(),
                    application.ScopeId,
                    WorkspaceStaffOnboardingTests.Now.AddMinutes(4),
                    application.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    projection.Revision,
                    isRestricted: false),
                CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Equal(1, staff.CallCount);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
    }

    [Fact]
    public async Task Restriction_release_converges_a_target_bound_terminal_application()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        FakeRepository applications = new(application);
        FakeRestrictionProjectionRepository restrictions = new(
            applications.Applications);
        using ServiceProvider provider = CreateProvider(
            applications,
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            restrictionProjections: restrictions);

        Result<WorkspaceStaffOnboardingDto> first = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            restrictions.Get(application.Id);
        Assert.True(projection.Apply(
            expectedRevision: 0,
            WorkspaceStaffOnboardingProcessingRestrictionContract.CurrentVersion,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3)).IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider
            .GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None));
        Assert.True(application.Supersede(
            WorkspaceStaffOnboardingTests.Now.AddMinutes(4)).IsSuccess);
        Assert.Null(application.IdentityAnchorResolutionEventId);
        Assert.True(projection.Release(
            expectedRevision: 1,
            WorkspaceStaffOnboardingProcessingRestrictionContract.CurrentVersion,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(5)).IsSuccess);

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>()
            .HandleAsync(
                new WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent(
                    Guid.NewGuid(),
                    application.ScopeId,
                    WorkspaceStaffOnboardingTests.Now.AddMinutes(5),
                    application.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    projection.Revision,
                    isRestricted: false),
                CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(
            BunkFy.Modules.Workspaces.Domain
                .WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
    }

    [Theory]
    [InlineData(
        "Staff.StaffSuspended",
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    [InlineData(
        "Staff.StaffDeparted",
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    [InlineData(
        "Staff.StaffMemberNotFound",
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing)]
    public async Task Property_reconcile_failure_rereads_committed_lifecycle_before_marking_failed(
        string errorCode,
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle terminalLifecycle,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch terminalSubjectMatch)
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch =
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact;
        FakeStaffPropertyProvisioner properties = new()
        {
            ErrorCode = errorCode,
            BeforeResult = () =>
            {
                lifecycle = terminalLifecycle;
                subjectMatch = terminalSubjectMatch;
            }
        };
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                lifecycle,
                subjectMatch,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId));
        FakeAccessControl access = new();
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner
            {
                StaffMemberId = staffMemberId,
                ResolutionEventId = resolutionEventId
            },
            access,
            staffProperties: properties,
            anchorOutcomes: outcomes);

        Result<WorkspaceStaffOnboardingDto> first = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);

        Assert.Equal(1, properties.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Null(application.FailureCode);
        Assert.Equal(
            BunkFy.Modules.Workspaces.Domain
                .WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>());
        Assert.Equal(0, access.AssignmentCallCount);
    }

    [Fact]
    public async Task Property_reconcile_lifecycle_race_with_open_process_is_retryable_without_failed_state()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
        FakeStaffPropertyProvisioner properties = new()
        {
            ErrorCode = "Staff.StaffSuspended",
            BeforeResult = () => lifecycle =
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended
        };
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                lifecycle,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId));
        WorkspaceStaffAccessProcess open = WorkspaceStaffAccessProcess.Create(
            Guid.NewGuid(),
            application.ScopeId,
            staffMemberId,
            application.SubjectId,
            WorkspaceStaffAccessTargetState.Suspended,
            targetStaffVersion: 2,
            DateOnly.FromDateTime(
                WorkspaceStaffOnboardingTests.Now.UtcDateTime),
            "integration:staff",
            [],
            WorkspaceStaffOnboardingTests.Now).Value;
        SingleOpenAccessProcessRepository accessProcesses = new(open);
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner
            {
                StaffMemberId = staffMemberId,
                ResolutionEventId = resolutionEventId
            },
            new FakeAccessControl(),
            staffProperties: properties,
            anchorOutcomes: outcomes,
            accessProcesses: accessProcesses);

        Result<WorkspaceStaffOnboardingDto> first = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None));

        Assert.Contains(
            WorkspaceStaffOnboardingApplicationErrors
                .IdentityAnchorLifecycleTransitionPending.Code,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, properties.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Null(application.FailureCode);
        Assert.Null(application.IdentityAnchorResolutionEventId);
    }

    [Fact]
    public async Task Property_reconcile_error_is_preserved_only_when_final_authority_remains_active()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        FakeStaffPropertyProvisioner properties = new()
        {
            ErrorCode = "Staff.StaffSuspended"
        };
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId));
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner
            {
                StaffMemberId = staffMemberId,
                ResolutionEventId = resolutionEventId
            },
            new FakeAccessControl(),
            staffProperties: properties,
            anchorOutcomes: outcomes);

        Result<WorkspaceStaffOnboardingDto> first = await provider
            .GetRequiredService<ICommandHandler<
                RetryWorkspaceStaffOnboardingCommand,
                WorkspaceStaffOnboardingDto>>()
            .HandleAsync(
                new RetryWorkspaceStaffOnboardingCommand(application.Id),
                CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = CreateContinuationFact(application);

        await provider.GetRequiredService<
                WorkspaceStaffOnboardingIdentityAnchorContinuationHandler>()
            .HandleAsync(continuation, CancellationToken.None);

        Assert.Equal(1, properties.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal("Staff.StaffSuspended", application.FailureCode);
        Assert.Null(application.IdentityAnchorResolutionEventId);
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

    [Fact]
    public async Task Staff_anchor_found_for_local_submitted_application_commits_redaction_without_disclosure()
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "Profile A");
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        RecordingUnitOfWork unitOfWork = new();
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId));
        using ServiceProvider provider = CreateProvider(
            new FakeRepository(application),
            new FakeStaffProvisioner(),
            new FakeAccessControl(),
            new FakeJoinTokenInspector(
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId),
            anchorOutcomes: outcomes,
            unitOfWork: unitOfWork);

        Result<WorkspaceStaffOnboardingSubmissionOutcome> result =
            await provider.GetRequiredService<
                    IWorkspaceStaffOnboardingSubmitter>()
                .SubmitWithAuthorityOutcomeAsync(
                    new SubmitWorkspaceStaffOnboardingCommand(
                        WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                        "secret-token",
                        application.SubjectId,
                        "Must not be written",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                    CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingSubmissionOutcomeKind
                .AuthorityMovedToStaff,
            result.Value.Kind);
        Assert.Null(result.Value.Application);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
        Assert.Null(application.DisplayName);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Single(application.DomainEvents.OfType<
            WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>());
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
    public async Task Terminal_source_replay_with_local_staff_coordinates_is_not_disclosed(
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
            Guid.NewGuid(),
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

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.IdentityAnchorConflict,
            replayed.Error);
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

    private static WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
        CreateContinuationFact(WorkspaceStaffOnboarding application)
    {
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            continuation = Assert.Single(application.DomainEvents.OfType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>());
        return new(
            continuation.EventId,
            continuation.ScopeId,
            continuation.OccurredAtUtc,
            continuation.ApplicationId,
            continuation.StaffMemberId);
    }

    private static WorkspaceStaffOnboarding CreateRolledBackStaffReady(
        WorkspaceStaffOnboarding source,
        Guid claimId,
        Guid staffMemberId)
    {
        WorkspaceStaffOnboarding restored = CreateApplication(
            source.Id,
            source.SourceKind,
            source.SourceId,
            "Ada Operator");
        Assert.True(restored.ObserveClaimAccepted(
            claimId,
            1,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Assert.True(source.AcceptedMembershipId.HasValue);
        Assert.True(source.AcceptedMembershipVersion.HasValue);
        Assert.True(restored.BindAcceptedMembership(
            source.AcceptedMembershipId.Value,
            source.AcceptedMembershipVersion.Value,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1)).IsSuccess);
        Assert.True(restored.MarkStaffReady(
            staffMemberId,
            source.IdentityAnchorExpectedResolutionEventId!.Value,
            source.IdentityAnchorContinuationEventId!.Value,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2)).IsSuccess);
        restored.ClearDomainEvents();
        return restored;
    }

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
        FakeOrganizationEnrollmentClaimInspector? claims = null,
        FakeRestrictionProjectionRepository? restrictionProjections = null,
        FakeOperationLock? operationLock = null,
        IStaffPropertyAssignmentProvisioner? staffProperties = null,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader? anchorOutcomes =
            null,
        IWorkspaceStaffAccessProcessRepository? accessProcesses = null,
        IUnitOfWork? unitOfWork = null)
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
            restrictionProjections ??
                new FakeRestrictionProjectionRepository(
                    applications.Applications));
        services.AddSingleton<IWorkspaceStaffOnboardingOperationLock>(
            operationLock ?? new FakeOperationLock());
        services.AddSingleton<IWorkspaceStaffAccessProcessRepository>(
            accessProcesses ??
                WorkspaceStaffAccessMutationTestSupport.NoOpenProcesses);
        services.AddSingleton<IWorkspaceStaffAccessOperationLock>(
            new FakeStaffAccessOperationLock());
        services.AddSingleton<IWorkspaceStaffAccessPlanRepository>(new FakeAccessPlanRepository(plans));
        services.AddSingleton<IWorkspaceStaffDeferredClaimWithdrawalRepository>(
            deferredWithdrawals ??
                new FakeWorkspaceStaffDeferredClaimWithdrawalRepository());
        services.AddSingleton<IStaffOnboardingProvisioner>(staff);
        services.AddSingleton<IStaffOnboardingStateVerifier>(
            staffStateVerifiers ?? new FakeStaffStateVerifier());
        services.AddSingleton<IOrganizationMembershipReader>(
            memberships ?? new FakeMembershipReader());
        services.AddSingleton<IOrganizationScopeLifecycle>(
            new FakeOrganizationScopeLifecycle());
        services.AddSingleton(
            anchorOutcomes ??
            new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                request =>
                {
                    if (staff.LastRequest?.OperationId != request.ApplicationId ||
                        !staff.StaffMemberId.HasValue)
                    {
                        return StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                            .Absent(request);
                    }

                    return new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                        request.ApplicationId,
                        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                            .Unresolved,
                        staff.StaffMemberId.Value,
                        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                            .Active,
                        string.Equals(
                            staff.LastRequest.AuthSubjectId,
                            request.ExpectedAuthSubjectId,
                            StringComparison.Ordinal)
                                ? StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                    .Exact
                                : StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                    .Mismatch,
                        WorkspaceApplicationVersion: null,
                        ResolutionDisposition: null,
                        staff.ResolutionEventId);
                }));
        services.AddSingleton<IStaffPropertyAssignmentProvisioner>(
            staffProperties ?? new FakeStaffPropertyProvisioner());
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
        services.AddSingleton<IUnitOfWork>(
            unitOfWork ?? new TestUnitOfWork());
        services.AddWorkspacesApplication(new ConfigurationBuilder().Build(), "global");
        return services.BuildServiceProvider();
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public string ModuleName => WorkspacesModuleMetadata.Name;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public string ModuleName => WorkspacesModuleMetadata.Name;
        public int SaveCount { get; private set; }

        public Task SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            this.SaveCount++;
            return Task.CompletedTask;
        }
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

        public Task<WorkspaceStaffOnboardingSummaryListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) => Task.FromResult(
                new WorkspaceStaffOnboardingSummaryListResponse(
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

        public void Replace(WorkspaceStaffOnboarding application)
        {
            int index = this.applications.FindIndex(item => item.Id == application.Id);
            Assert.True(index >= 0);
            this.applications[index] = application;
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

        public WorkspaceStaffOnboardingProcessingRestrictionProjection Get(
            Guid applicationId) => this.projections[applicationId];
    }

    private sealed class FakeOperationLock(List<string>? calls = null)
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls?.Add("source-read");
            return Task.CompletedTask;
        }

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls?.Add("source-write");
            return Task.CompletedTask;
        }

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls?.Add("application");
            return Task.FromResult(true);
        }
    }

    private sealed class FakeStaffAccessOperationLock
        : IWorkspaceStaffAccessOperationLock
    {
        public Task AcquireSubjectAsync(
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AcquireStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireCoordinatesAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> TryAcquireProcessAsync(
            Guid processId,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> TryAcquireStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class SingleOpenAccessProcessRepository(
        WorkspaceStaffAccessProcess open)
        : IWorkspaceStaffAccessProcessRepository
    {
        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(
                open.Id == processId ? open : null);

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(
                open.StaffMemberId == staffMemberId &&
                open.TargetStaffVersion == targetStaffVersion
                    ? open
                    : null);

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(
                open.StaffMemberId == staffMemberId ? open : null);

        public Task<WorkspaceStaffAccessProcess?>
            GetLatestCompletedSuspensionAsync(
                Guid staffMemberId,
                string subjectId,
                CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(null);

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(null);

        public Task<WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessProcess process,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStaffProvisioner : IStaffOnboardingProvisioner
    {
        public Guid? StaffMemberId { get; init; } = Guid.NewGuid();
        public Guid ResolutionEventId { get; init; } = Guid.NewGuid();
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
                ? new StaffOnboardingProvisioningResult(
                    true,
                    this.StaffMemberId,
                    ErrorCode: null,
                    ResolutionEventId: this.ResolutionEventId)
                : new StaffOnboardingProvisioningResult(false, null, this.ErrorCode));
        }
    }

    private sealed class FakeStaffPropertyProvisioner
        : IStaffPropertyAssignmentProvisioner
    {
        public string? ErrorCode { get; init; }
        public Action? BeforeResult { get; init; }
        public int CallCount { get; private set; }

        public Task<StaffPropertyAssignmentProvisioningResult> ReconcileAsync(
            StaffPropertyAssignmentProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            this.BeforeResult?.Invoke();
            return Task.FromResult(this.ErrorCode is null
                ? new StaffPropertyAssignmentProvisioningResult(
                    true,
                    request.PropertyIds.ToArray(),
                    null)
                : new StaffPropertyAssignmentProvisioningResult(
                    false,
                    [],
                    this.ErrorCode));
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
        public bool CancelAssignments { get; set; }
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
            if (this.CancelAssignments)
            {
                return Task.FromException(
                    new OperationCanceledException("Access attempt was cancelled."));
            }

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
