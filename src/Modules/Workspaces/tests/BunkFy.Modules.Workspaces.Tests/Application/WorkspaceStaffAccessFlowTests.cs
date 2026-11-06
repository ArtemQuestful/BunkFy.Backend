namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ContractRestorationDisposition =
    BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffAccessRestorationDisposition;
using DomainRestorationDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffAccessRestorationDisposition;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffAccessFlowTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly string ScopeId = OrganizationId.ToString("D");
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Suspension_preparation_persists_the_exact_profile_snapshot()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto custom = profiles.AddProfile(scope, "night-auditor");
        profiles.Assign(subject, scope, custom.Id);
        FakeProcessRepository repository = new();
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            new FakeRestorationSuppressionReader(),
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(roles, profiles),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Suspend,
            StaffStatus.Active,
            StaffStatus.Suspended,
            expectedVersion: 1);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.True(result.Value.RequiresAccessDenial);
        Assert.Equal(
            ContractRestorationDisposition.NotApplicable,
            result.Value.RestorationDisposition);
        Assert.NotEqual(Guid.Empty, result.Value.ProcessId);
        WorkspaceStaffAccessProcess process = Assert.Single(repository.Processes);
        Assert.Equal(WorkspaceStaffAccessProcessState.Prepared, process.State);
        Assert.Equal(
            DomainRestorationDisposition.NotApplicable,
            process.RestorationDisposition);
        Assert.Equal([custom.Id], process.ProfileSnapshots.Select(snapshot => snapshot.ProfileId));
    }

    [Fact]
    public async Task Denial_changes_membership_before_removing_product_access()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        FakeMembershipLifecycle memberships = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto custom = profiles.AddProfile(scope, "night-auditor");
        profiles.Assign(subject, scope, custom.Id);
        roles.Add(subject, WorkspaceAccessRoles.MembershipMarker, scope);
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Suspended,
            targetVersion: 2,
            [custom.Id]);
        FakeProcessRepository repository = new(process);
        WorkspaceStaffAccessDenier denier = new(
            memberships,
            new WorkspaceAccessProvisioner(roles, profiles),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessDenier>.Instance);
        DenyWorkspaceStaffAccessCommandHandler handler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            denier);

        Result<WorkspaceStaffAccessCoordinationOutcome> result = await handler.HandleAsync(
            new DenyWorkspaceStaffAccessCommand(process.Id),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffAccessCoordinationOutcome.Allowed, result.Value);
        Assert.Equal(WorkspaceStaffAccessProcessState.AwaitingStaffCommit, process.State);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(subject, WorkspaceAccessRoles.MembershipMarker, scope));
        Assert.True(operations.IndexOf("membership:Suspended") < operations.IndexOf("profiles:reconcile"));
    }

    [Fact]
    public async Task Owner_protection_keeps_profiles_and_blocks_the_staff_transition()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        FakeMembershipLifecycle memberships = new(
            operations,
            OrganizationMembershipLifecycleOutcome.OwnerProtected);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto custom = profiles.AddProfile(scope, "manager");
        profiles.Assign(subject, scope, custom.Id);
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Suspended,
            targetVersion: 2,
            [custom.Id]);
        WorkspaceStaffAccessDenier denier = new(
            memberships,
            new WorkspaceAccessProvisioner(roles, profiles),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessDenier>.Instance);
        FakeProcessRepository repository = new(process);
        DenyWorkspaceStaffAccessCommandHandler handler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            denier);

        Result<WorkspaceStaffAccessCoordinationOutcome> result = await handler.HandleAsync(
            new DenyWorkspaceStaffAccessCommand(process.Id),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffAccessCoordinationOutcome.OwnerProtected, result.Value);
        Assert.Equal(WorkspaceStaffAccessProcessState.Prepared, process.State);
        Assert.Equal([custom.Id], profiles.AssignedProfileIds(subject, scope));
        Assert.Equal("Workspaces.StaffAccessOwnerProtected", process.FailureCode);
    }

    [Fact]
    public async Task Resume_after_completed_redacted_onboarding_copies_the_latest_completed_suspension_snapshot()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        WorkspaceStaffAccessProcess suspension = CreateProcess(
            WorkspaceStaffAccessTargetState.Suspended,
            targetVersion: 2,
            [first, second]);
        Assert.True(suspension.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(suspension.ObserveStaffCommit(Now).IsSuccess);
        FakeProcessRepository repository = new(suspension);
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            new FakeRestorationSuppressionReader(),
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(new FakeRoles([]), new FakeProfiles([])),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.False(result.Value.RequiresAccessDenial);
        Assert.Equal(
            ContractRestorationDisposition.RestoreSnapshot,
            result.Value.RestorationDisposition);
        Assert.NotEqual(Guid.Empty, result.Value.ProcessId);
        WorkspaceStaffAccessProcess resume = repository.Processes.Single(process => process.Id != suspension.Id);
        Assert.Equal(WorkspaceStaffAccessProcessState.AwaitingStaffCommit, resume.State);
        Assert.Equal(
            DomainRestorationDisposition.RestoreSnapshot,
            resume.RestorationDisposition);
        Assert.Equal(
            new[] { first, second }.Order(),
            resume.ProfileSnapshots.Select(snapshot => snapshot.ProfileId).Order());
    }

    [Fact]
    public async Task Terminal_onboarding_resolution_persists_suppressed_resume_and_completes_without_access_work()
    {
        List<string> operations = [];
        Guid profileId = Guid.NewGuid();
        WorkspaceStaffAccessProcess suspension = CreateProcess(
            WorkspaceStaffAccessTargetState.Suspended,
            targetVersion: 2,
            [profileId]);
        Assert.True(suspension.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(suspension.ObserveStaffCommit(Now).IsSuccess);
        FakeProcessRepository repository = new(suspension);
        FakeRestorationSuppressionReader suppressions = new(
            WorkspaceStaffOnboardingRestorationSuppressionState.Suppressed);
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            suppressions,
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(
                new FakeRoles(operations),
                new FakeProfiles(operations)),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.False(result.Value.RequiresAccessDenial);
        Assert.Equal(
            ContractRestorationDisposition.Suppressed,
            result.Value.RestorationDisposition);
        Assert.NotEqual(Guid.Empty, result.Value.ProcessId);
        WorkspaceStaffAccessProcess resume = repository.Processes.Single(
            process => process.Id == result.Value.ProcessId);
        Assert.Equal(WorkspaceStaffAccessTargetState.Active, resume.TargetState);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit,
            resume.State);
        Assert.Equal(
            DomainRestorationDisposition.Suppressed,
            resume.RestorationDisposition);
        Assert.Empty(resume.ProfileSnapshots);
        Assert.Equal(2, repository.Processes.Count);
        Assert.Empty(operations);
        Assert.Equal(1, suppressions.ReadCount);

        FakeMembershipLifecycle memberships = new(operations);
        WorkspaceAccessProvisioner access = new(
            new FakeRoles(operations),
            new FakeProfiles(operations));
        WorkspaceStaffAccessRestorer restorer = new(
            memberships,
            access,
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessRestorer>.Instance);
        StaffLifecycleWorkspaceAccessHandler lifecycleHandler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            restorer,
            new TestClock());

        await lifecycleHandler.HandleAsync(
            new StaffMemberLifecycleChangedIntegrationEvent(
                Guid.NewGuid(),
                ScopeId,
                Now,
                StaffId,
                StaffStatus.Active,
                new DateOnly(2026, 7, 21),
                3,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffAccessProcessState.Completed, resume.State);
        Assert.Empty(operations);

        WorkspaceStaffAccessDenier denier = new(
            memberships,
            access,
            new TestClock(),
            NullLogger<WorkspaceStaffAccessDenier>.Instance);
        RetryWorkspaceStaffAccessProcessCommandHandler retryHandler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            denier,
            restorer);

        Result<WorkspaceStaffAccessProcessDto> retry = await retryHandler.HandleAsync(
            new RetryWorkspaceStaffAccessProcessCommand(resume.Id),
            CancellationToken.None);

        Assert.True(retry.IsSuccess, retry.Error.Code);
        Assert.Equal(WorkspaceStaffAccessProcessStatus.Completed, retry.Value.Status);
        Assert.Equal(
            ContractRestorationDisposition.Suppressed,
            retry.Value.RestorationDisposition);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Repeated_and_future_resume_attempts_remain_suppressed()
    {
        List<string> operations = [];
        FakeProcessRepository repository = new();
        FakeRestorationSuppressionReader suppressions = new(
            WorkspaceStaffOnboardingRestorationSuppressionState.Suppressed);
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            suppressions,
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(
                new FakeRoles(operations),
                new FakeProfiles(operations)),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext replay = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);
        StaffLifecyclePolicyContext future = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 3);

        Result<WorkspaceStaffAccessPreparation> replayResult = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(replay),
            CancellationToken.None);
        Result<WorkspaceStaffAccessPreparation> replayResultAgain = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(replay),
            CancellationToken.None);

        Assert.True(replayResult.IsSuccess, replayResult.Error.Code);
        Assert.True(replayResultAgain.IsSuccess, replayResultAgain.Error.Code);
        Assert.False(replayResult.Value.RequiresAccessDenial);
        Assert.False(replayResultAgain.Value.RequiresAccessDenial);
        Assert.Equal(
            ContractRestorationDisposition.Suppressed,
            replayResult.Value.RestorationDisposition);
        Assert.Equal(
            ContractRestorationDisposition.Suppressed,
            replayResultAgain.Value.RestorationDisposition);
        Assert.Equal(replayResult.Value.ProcessId, replayResultAgain.Value.ProcessId);
        WorkspaceStaffAccessProcess first = Assert.Single(repository.Processes);
        Assert.Equal(
            DomainRestorationDisposition.Suppressed,
            first.RestorationDisposition);
        Assert.Equal(1, suppressions.ReadCount);

        Assert.True(first.ObserveStaffCommit(Now).IsSuccess);
        Result<WorkspaceStaffAccessPreparation> futureResult = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(future),
            CancellationToken.None);

        Assert.True(futureResult.IsSuccess, futureResult.Error.Code);
        Assert.False(futureResult.Value.RequiresAccessDenial);
        Assert.Equal(
            ContractRestorationDisposition.Suppressed,
            futureResult.Value.RestorationDisposition);
        Assert.NotEqual(first.Id, futureResult.Value.ProcessId);
        WorkspaceStaffAccessProcess second = repository.Processes.Single(
            process => process.Id == futureResult.Value.ProcessId);
        Assert.Equal(
            DomainRestorationDisposition.Suppressed,
            second.RestorationDisposition);
        Assert.Empty(second.ProfileSnapshots);
        Assert.Equal(2, suppressions.ReadCount);
        Assert.Empty(operations);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingRestorationSuppressionState.Conflict)]
    [InlineData((WorkspaceStaffOnboardingRestorationSuppressionState)0)]
    public async Task Non_authoritative_onboarding_resolution_ledger_fails_resume_closed(
        WorkspaceStaffOnboardingRestorationSuppressionState suppressionState)
    {
        FakeProcessRepository repository = new();
        FakeRestorationSuppressionReader suppressions = new(
            suppressionState);
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            suppressions,
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(new FakeRoles([]), new FakeProfiles([])),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffAccessApplicationErrors.ProcessConflict,
            result.Error);
        Assert.Empty(repository.Processes);
        Assert.Equal(1, suppressions.ReadCount);
    }

    [Fact]
    public async Task Unlinked_resume_does_not_restore_previous_subject_access()
    {
        UnexpectedRequestDispatcher dispatcher = new();
        WorkspaceStaffLifecyclePolicy policy = new(
            dispatcher,
            NullLogger<WorkspaceStaffLifecyclePolicy>.Instance);
        StaffLifecyclePolicyContext context = new(
            Guid.NewGuid(),
            ScopeId,
            StaffId,
            authSubjectId: null,
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            new DateOnly(2026, 7, 21),
            expectedVersion: 3,
            targetVersion: 4,
            "user:owner");

        StaffLifecyclePolicyDecision decision = await policy.PrepareAsync(
            context,
            CancellationToken.None);

        Assert.Equal(StaffLifecyclePolicyDecision.Allowed, decision);
        Assert.Equal(0, dispatcher.SendCount);
    }

    [Fact]
    public async Task Immediate_resume_uses_the_observed_suspension_without_a_stale_persistence_read()
    {
        Guid profileId = Guid.NewGuid();
        WorkspaceStaffAccessProcess suspension = CreateProcess(
            WorkspaceStaffAccessTargetState.Suspended,
            targetVersion: 2,
            [profileId]);
        Assert.True(suspension.MarkAwaitingStaffCommit(Now).IsSuccess);
        FakeProcessRepository repository = new(suspension)
        {
            FailLatestSuspensionRead = true
        };
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            new FakeRestorationSuppressionReader(),
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(new FakeRoles([]), new FakeProfiles([])),
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(WorkspaceStaffAccessProcessState.Completed, suspension.State);
        WorkspaceStaffAccessProcess resume = repository.Processes.Single(process => process.Id != suspension.Id);
        Assert.Equal([profileId], resume.ProfileSnapshots.Select(snapshot => snapshot.ProfileId));
    }

    [Fact]
    public async Task Restricted_workspace_blocks_staff_reactivation_before_process_mutation()
    {
        FakeProcessRepository repository = new();
        PrepareWorkspaceStaffAccessCommandHandler handler = new(
            repository,
            new FakeRestorationSuppressionReader(),
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            new WorkspaceAccessProvisioner(new FakeRoles([]), new FakeProfiles([])),
            WorkspaceOperationalAdmissionTestSupport.Restricted(ScopeId),
            new TestClock(),
            NullLogger<PrepareWorkspaceStaffAccessCommandHandler>.Instance);
        StaffLifecyclePolicyContext context = CreateContext(
            StaffLifecycleTransition.Resume,
            StaffStatus.Suspended,
            StaffStatus.Active,
            expectedVersion: 2);

        Result<WorkspaceStaffAccessPreparation> result = await handler.HandleAsync(
            new PrepareWorkspaceStaffAccessCommand(context),
            CancellationToken.None);

        Assert.Equal(
            WorkspaceOperationalAdmissionErrors.ProcessingRestricted,
            result.Error);
        Assert.Empty(repository.Processes);
    }

    [Fact]
    public async Task Active_staff_event_restores_membership_marker_and_exact_profiles()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        FakeMembershipLifecycle memberships = new(operations);
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessProfileDto first = profiles.AddProfile(scope, "front-desk");
        profiles.AddProfile(scope, "not-restored");
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Active,
            targetVersion: 3,
            [first.Id]);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        FakeProcessRepository repository = new(process);
        WorkspaceAccessProvisioner access = new(roles, profiles);
        WorkspaceStaffAccessRestorer restorer = new(
            memberships,
            access,
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessRestorer>.Instance);
        StaffLifecycleWorkspaceAccessHandler handler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            restorer,
            new TestClock());

        await handler.HandleAsync(new StaffMemberLifecycleChangedIntegrationEvent(
            Guid.NewGuid(),
            ScopeId,
            Now,
            process.StaffMemberId,
            StaffStatus.Active,
            new DateOnly(2026, 7, 21),
            3,
            "user:owner"), CancellationToken.None);

        Assert.Equal(WorkspaceStaffAccessProcessState.Completed, process.State);
        Assert.True(roles.Has(subject, WorkspaceAccessRoles.MembershipMarker, scope));
        Assert.Equal([first.Id], profiles.AssignedProfileIds(subject, scope));
        Assert.True(operations.IndexOf("membership:Active") < operations.IndexOf("profiles:reconcile"));
    }

    [Fact]
    public async Task Retry_completes_a_pending_restoration_with_the_exact_snapshot()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        FakeMembershipLifecycle memberships = new(operations);
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessProfileDto restored = profiles.AddProfile(scope, "front-desk");
        profiles.AddProfile(scope, "not-restored");
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Active,
            targetVersion: 3,
            [restored.Id]);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(process.ObserveStaffCommit(Now).IsSuccess);
        Assert.True(process.RecordFailure("Workspaces.StaffAccessRestoreFailed", Now).IsSuccess);
        FakeProcessRepository repository = new(process);
        WorkspaceAccessProvisioner access = new(roles, profiles);
        WorkspaceStaffAccessDenier denier = new(
            memberships,
            access,
            new TestClock(),
            NullLogger<WorkspaceStaffAccessDenier>.Instance);
        WorkspaceStaffAccessRestorer restorer = new(
            memberships,
            access,
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessRestorer>.Instance);
        RetryWorkspaceStaffAccessProcessCommandHandler handler = new(
            WorkspaceStaffAccessMutationTestSupport.Create(repository),
            denier,
            restorer);

        Result<WorkspaceStaffAccessProcessDto> result = await handler.HandleAsync(
            new RetryWorkspaceStaffAccessProcessCommand(process.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(WorkspaceStaffAccessProcessStatus.Completed, result.Value.Status);
        Assert.Null(result.Value.FailureCode);
        Assert.True(roles.Has(subject, WorkspaceAccessRoles.MembershipMarker, scope));
        Assert.Equal([restored.Id], profiles.AssignedProfileIds(subject, scope));
    }

    [Fact]
    public async Task Restricted_workspace_keeps_pending_restoration_retryable()
    {
        List<string> operations = [];
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Active,
            targetVersion: 3,
            []);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(process.ObserveStaffCommit(Now).IsSuccess);
        WorkspaceStaffAccessRestorer restorer = new(
            new FakeMembershipLifecycle(operations),
            new WorkspaceAccessProvisioner(
                new FakeRoles(operations),
                new FakeProfiles(operations)),
            WorkspaceOperationalAdmissionTestSupport.Restricted(ScopeId),
            new TestClock(),
            NullLogger<WorkspaceStaffAccessRestorer>.Instance);

        WorkspaceStaffAccessCoordinationOutcome outcome =
            await restorer.RestoreAsync(process, CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffAccessCoordinationOutcome.RetryRequired,
            outcome);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.RestorationPending,
            process.State);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Staff_anonymisation_reasserts_departure_access_closure()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto profile = profiles.AddProfile(scope, "front-desk");
        profiles.Assign(subject, scope, profile.Id);
        roles.Add(subject, WorkspaceAccessRoles.MembershipMarker, scope);
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Departed,
            targetVersion: 2,
            [profile.Id]);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(process.ObserveStaffCommit(Now).IsSuccess);
        FakeCorrelationRepository correlations = new();
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
            new FakeStaffRestoreStateReader(new(
                StaffId,
                Version: 2,
                StaffAnonymisationRestoreRecordState.Departed,
                "member-a",
                AnonymisedAtUtc: null)),
            new FakeProcessRepository(process),
            new WorkspaceStaffAccessDenier(
                new FakeMembershipLifecycle(operations),
                new WorkspaceAccessProvisioner(roles, profiles),
                new TestClock(),
                NullLogger<WorkspaceStaffAccessDenier>.Instance),
            correlations);

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateAnonymisationRequest(),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus.Completed,
            result.Status);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));
        Assert.True(
            operations.IndexOf("membership:Removed") <
            operations.IndexOf("profiles:reconcile"));
        Assert.Equal(0, correlations.ScrubCount);
    }

    [Fact]
    public async Task Staff_retention_reasserts_departure_access_closure()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto profile =
            profiles.AddProfile(scope, "front-desk");
        profiles.Assign(subject, scope, profile.Id);
        roles.Add(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope);
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Departed,
            targetVersion: 2,
            [profile.Id]);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(process.ObserveStaffCommit(Now).IsSuccess);
        FakeCorrelationRepository correlations = new();
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
            new FakeStaffRestoreStateReader(new(
                StaffId,
                Version: 2,
                StaffAnonymisationRestoreRecordState.Departed,
                "member-a",
                AnonymisedAtUtc: null)),
            new FakeProcessRepository(process),
            new WorkspaceStaffAccessDenier(
                new FakeMembershipLifecycle(operations),
                new WorkspaceAccessProvisioner(roles, profiles),
                new TestClock(),
                NullLogger<WorkspaceStaffAccessDenier>.Instance),
            correlations);

        StaffRetentionAnonymisationPrerequisiteResult result =
            await prerequisite.PrepareAsync(
                CreateRetentionRequest(),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionAnonymisationPrerequisiteStatus.Completed,
            result.Status);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));
        Assert.True(
            operations.IndexOf("membership:Removed") <
            operations.IndexOf("profiles:reconcile"));
        Assert.Equal(1, correlations.RequestCount);
        Assert.Equal(1, correlations.ScrubCount);
        Assert.NotNull(correlations.Receipt);

        StaffRetentionAnonymisationPrerequisiteResult replay =
            await prerequisite.PrepareAsync(
                CreateRetentionRequest(),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionAnonymisationPrerequisiteStatus.Completed,
            replay.Status);
        Assert.Equal(1, correlations.RequestCount);
        Assert.Equal(1, correlations.ScrubCount);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));

        StaffRetentionAnonymisationPrerequisiteResult verified =
            await prerequisite.VerifyAsync(
                CreateRetentionRequest(),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionAnonymisationPrerequisiteStatus.Completed,
            verified.Status);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));
    }

    [Fact]
    public async Task Staff_retention_blocks_divergent_existing_scrub_receipt()
    {
        FakeCorrelationRepository correlations = new();
        correlations.SeedReceipt(
            WorkspaceStaffRetentionCorrelationReceipt.Create(
                CorrelationReceiptId,
                "different-tenant",
                Guid.NewGuid(),
                StaffId,
                selectedStaffVersion: 2,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 1,
                Now).Value);
        List<string> operations = [];
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
                new FakeStaffRestoreStateReader(new(
                    StaffId,
                    Version: 2,
                    StaffAnonymisationRestoreRecordState.Departed,
                    "member-a",
                    AnonymisedAtUtc: null)),
                new FakeProcessRepository(),
                new WorkspaceStaffAccessDenier(
                    new FakeMembershipLifecycle(operations),
                    new WorkspaceAccessProvisioner(
                        new FakeRoles(operations),
                        new FakeProfiles(operations)),
                    new TestClock(),
                    NullLogger<WorkspaceStaffAccessDenier>.Instance),
                correlations);

        StaffRetentionAnonymisationPrerequisiteResult result =
            await prerequisite.PrepareAsync(
                CreateRetentionRequest(),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionAnonymisationPrerequisiteStatus.Blocked,
            result.Status);
        Assert.Equal(
            "Workspaces.StaffRetentionCorrelationReceiptInvalid",
            result.OutcomeCode);
        Assert.Equal(0, correlations.RequestCount);
        Assert.Equal(0, correlations.ScrubCount);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Staff_retention_blocks_without_durable_departure_mapping()
    {
        List<string> operations = [];
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
            new FakeStaffRestoreStateReader(new(
                StaffId,
                Version: 2,
                StaffAnonymisationRestoreRecordState.Departed,
                "member-a",
                AnonymisedAtUtc: null)),
            new FakeProcessRepository(),
            new WorkspaceStaffAccessDenier(
                new FakeMembershipLifecycle(operations),
                new WorkspaceAccessProvisioner(
                    new FakeRoles(operations),
                    new FakeProfiles(operations)),
                new TestClock(),
                NullLogger<WorkspaceStaffAccessDenier>.Instance));

        StaffRetentionAnonymisationPrerequisiteResult result =
            await prerequisite.PrepareAsync(
                CreateRetentionRequest(),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionAnonymisationPrerequisiteStatus.Blocked,
            result.Status);
        Assert.Equal(
            "Workspaces.StaffAnonymisationAccessMappingUnavailable",
            result.OutcomeCode);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Staff_restore_requires_durable_departure_mapping()
    {
        List<string> operations = [];
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
            new FakeStaffRestoreStateReader(new(
                StaffId,
                Version: 3,
                StaffAnonymisationRestoreRecordState.Anonymised,
                AuthSubjectId: null,
                Now)),
            new FakeProcessRepository(),
            new WorkspaceStaffAccessDenier(
                new FakeMembershipLifecycle(operations),
                new WorkspaceAccessProvisioner(
                    new FakeRoles(operations),
                    new FakeProfiles(operations)),
                new TestClock(),
                NullLogger<WorkspaceStaffAccessDenier>.Instance));

        DataRightsAnonymisationRestorePrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateRestoreRequest(),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationRestorePrerequisiteStatus.Blocked,
            result.Status);
        Assert.Equal(
            "Workspaces.StaffAnonymisationAccessMappingUnavailable",
            result.OutcomeCode);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Staff_restore_uses_departure_mapping_to_reassert_denial()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto profile = profiles.AddProfile(scope, "front-desk");
        profiles.Assign(subject, scope, profile.Id);
        roles.Add(subject, WorkspaceAccessRoles.MembershipMarker, scope);
        WorkspaceStaffAccessProcess process = CreateProcess(
            WorkspaceStaffAccessTargetState.Departed,
            targetVersion: 2,
            [profile.Id]);
        Assert.True(process.MarkAwaitingStaffCommit(Now).IsSuccess);
        Assert.True(process.ObserveStaffCommit(Now).IsSuccess);
        FakeCorrelationRepository correlations = new();
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
            new FakeStaffRestoreStateReader(new(
                StaffId,
                Version: 3,
                StaffAnonymisationRestoreRecordState.Anonymised,
                AuthSubjectId: null,
                AnonymisedAtUtc: Now)),
            new FakeProcessRepository(process),
            new WorkspaceStaffAccessDenier(
                new FakeMembershipLifecycle(operations),
                new WorkspaceAccessProvisioner(roles, profiles),
                new TestClock(),
                NullLogger<WorkspaceStaffAccessDenier>.Instance),
            correlations);

        DataRightsAnonymisationRestorePrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateRestoreRequest(),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationRestorePrerequisiteStatus.Completed,
            result.Status);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));
        Assert.True(
            operations.IndexOf("membership:Removed") <
            operations.IndexOf("profiles:reconcile"));
        Assert.Equal(0, correlations.ScrubCount);
    }

    [Fact]
    public async Task Staff_anonymisation_accepts_exact_workspace_first_proof()
    {
        WorkspaceStaffCorrelationAnonymisationReceipt receipt =
            WorkspaceStaffCorrelationAnonymisationReceipt.Create(
                Guid.NewGuid(),
                ScopeId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 4,
                operationRevision: 5,
                Guid.NewGuid(),
                StaffId,
                selectedStaffVersion: 2,
                selectedAnchorVersion: 4,
                resultingAnchorVersion: 5,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 0,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "user:privacy-executor",
                Now).Value;
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            WorkspaceStaffCorrelationAnonymisationTombstone
                .Create(receipt).Value;
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            new(
                WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                    .Eligible,
                receipt.AnchorProcessId,
                receipt.ResultingAnchorVersion,
                StaffId,
                SelectedStaffVersion: 2,
                receipt.CreateSubjectPseudonym(),
                receipt.ResultingStateSha256,
                OnboardingRecordCount: 1,
                AccessProcessRecordCount: 1,
                AccessPlanRecordCount: 0);
        List<string> operations = [];
        WorkspaceStaffAnonymisationAccessPrerequisite prerequisite =
            CreateAccessPrerequisite(
                new FakeStaffRestoreStateReader(new(
                    StaffId,
                    Version: 2,
                    StaffAnonymisationRestoreRecordState.Departed,
                    "member-a",
                    AnonymisedAtUtc: null)),
                new FakeProcessRepository(),
                new WorkspaceStaffAccessDenier(
                    new FakeMembershipLifecycle(operations),
                    new WorkspaceAccessProvisioner(
                        new FakeRoles(operations),
                        new FakeProfiles(operations)),
                    new TestClock(),
                    NullLogger<
                        WorkspaceStaffAccessDenier>.Instance),
                dataRightsCorrelations:
                    new NoDataRightsCorrelationRepository(
                        tombstone,
                        snapshot));

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateAnonymisationRequest(),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus
                .Completed,
            result.Status);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Workspace_anonymisation_reasserts_access_closure_before_scrub()
    {
        Guid anchorProcessId = Guid.NewGuid();
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject subject = AccessSubject.User("member-a");
        AccessScope scope = WorkspaceAccessScopes.Create(ScopeId);
        AccessProfileDto profile =
            profiles.AddProfile(scope, "front-desk");
        profiles.Assign(subject, scope, profile.Id);
        roles.Add(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope);
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            new(
                WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                    .Eligible,
                anchorProcessId,
                AnchorProcessVersion: 4,
                StaffId,
                SelectedStaffVersion: 2,
                "member-a",
                new string('a', 64),
                OnboardingRecordCount: 1,
                AccessProcessRecordCount: 1,
                AccessPlanRecordCount: 0);
        WorkspaceStaffCorrelationAnonymisationPrerequisite prerequisite =
            new(
                new NoDataRightsCorrelationRepository(
                    snapshot: snapshot),
                new WorkspaceStaffAccessDenier(
                    new FakeMembershipLifecycle(operations),
                    new WorkspaceAccessProvisioner(
                        roles,
                        profiles),
                    new TestClock(),
                    NullLogger<
                        WorkspaceStaffAccessDenier>.Instance),
                NullLogger<
                    WorkspaceStaffCorrelationAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateWorkspaceAnonymisationRequest(
                    anchorProcessId,
                    selectedAnchorVersion: 4),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus
                .Completed,
            result.Status);
        Assert.Empty(profiles.AssignedProfileIds(subject, scope));
        Assert.False(roles.Has(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope));
        Assert.True(
            operations.IndexOf("membership:Removed") <
            operations.IndexOf("profiles:reconcile"));
    }

    [Fact]
    public async Task Workspace_anonymisation_accepts_exact_committed_tombstone()
    {
        WorkspaceStaffCorrelationAnonymisationReceipt receipt =
            CreateWorkspaceCorrelationReceipt();
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            WorkspaceStaffCorrelationAnonymisationTombstone
                .Create(receipt).Value;
        WorkspaceStaffCorrelationAnonymisationSnapshot anonymised =
            CreateAnonymisedWorkspaceSnapshot(receipt);
        List<string> operations = [];
        WorkspaceStaffCorrelationAnonymisationPrerequisite prerequisite =
            new(
                new NoDataRightsCorrelationRepository(
                    tombstone,
                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        .Unavailable(),
                    anonymised),
                new WorkspaceStaffAccessDenier(
                    new FakeMembershipLifecycle(operations),
                    new WorkspaceAccessProvisioner(
                        new FakeRoles(operations),
                        new FakeProfiles(operations)),
                    new TestClock(),
                    NullLogger<
                        WorkspaceStaffAccessDenier>.Instance),
                NullLogger<
                    WorkspaceStaffCorrelationAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateWorkspaceAnonymisationRequest(
                    receipt.AnchorProcessId,
                    receipt.SelectedAnchorVersion),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus
                .Completed,
            result.Status);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Workspace_restore_rejects_mismatched_owner_proof()
    {
        WorkspaceStaffCorrelationAnonymisationReceipt receipt =
            CreateWorkspaceCorrelationReceipt();
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            WorkspaceStaffCorrelationAnonymisationTombstone
                .Create(receipt).Value;
        List<string> operations = [];
        WorkspaceStaffCorrelationAnonymisationPrerequisite prerequisite =
            new(
                new NoDataRightsCorrelationRepository(
                    tombstone,
                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        .Unavailable(),
                    CreateAnonymisedWorkspaceSnapshot(receipt)),
                new WorkspaceStaffAccessDenier(
                    new FakeMembershipLifecycle(operations),
                    new WorkspaceAccessProvisioner(
                        new FakeRoles(operations),
                        new FakeProfiles(operations)),
                    new TestClock(),
                    NullLogger<
                        WorkspaceStaffAccessDenier>.Instance),
                NullLogger<
                    WorkspaceStaffCorrelationAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationRestorePrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                CreateWorkspaceRestoreRequest(
                    receipt,
                    ownerReceiptSha256: new string('f', 64)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationRestorePrerequisiteStatus.Blocked,
            result.Status);
        Assert.Equal(
            "Workspaces.StaffCorrelationAnonymisationStateConflict",
            result.OutcomeCode);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Product_policy_denies_direct_organization_membership_changes()
    {
        WorkspaceOrganizationMembershipChangePolicy policy = new();

        OrganizationMembershipChangePolicyDecision decision = await policy.EvaluateAsync(
            new OrganizationMembershipChangePolicyRequest(
                OrganizationId,
                "owner",
                "member-a",
                OrganizationMembershipRole.Member,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipStatus.Suspended),
            CancellationToken.None);

        Assert.Equal(OrganizationMembershipChangePolicyDecision.Denied, decision);
    }

    private static StaffLifecyclePolicyContext CreateContext(
        StaffLifecycleTransition transition,
        StaffStatus previousStatus,
        StaffStatus targetStatus,
        long expectedVersion) => new(
        Guid.NewGuid(),
        ScopeId,
        StaffId,
        "member-a",
        transition,
        previousStatus,
        targetStatus,
        new DateOnly(2026, 7, 21),
        expectedVersion,
        expectedVersion + 1,
        "user:owner");

    private static readonly Guid StaffId = Guid.NewGuid();
    private static readonly Guid CorrelationReceiptId =
        Guid.NewGuid();

    private static WorkspaceStaffAnonymisationAccessPrerequisite
        CreateAccessPrerequisite(
            IStaffAnonymisationRestoreStateReader staffStateReader,
            IWorkspaceStaffAccessProcessRepository processRepository,
            WorkspaceStaffAccessDenier denier,
            FakeCorrelationRepository? correlations = null,
            IWorkspaceStaffCorrelationAnonymisationRepository?
                dataRightsCorrelations = null)
    {
        correlations ??= new FakeCorrelationRepository();
        WorkspaceStaffRetentionAccessClosure retentionAccessClosure = new(
            staffStateReader,
            processRepository,
            denier,
            NullLogger<
                WorkspaceStaffRetentionAccessClosure>.Instance);
        return new(
            staffStateReader,
            processRepository,
            correlations,
            dataRightsCorrelations ??
                new NoDataRightsCorrelationRepository(),
            new FakeRequestDispatcher(
                correlations,
                retentionAccessClosure),
            denier,
            NullLogger<
                WorkspaceStaffAnonymisationAccessPrerequisite>.Instance);
    }

    private static DataRightsAnonymisationContributionRequestV2
        CreateAnonymisationRequest() =>
        new(
            DataRightsAnonymisationContractV2.CurrentVersion,
            ScopeId,
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            PropertyId: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalRevision: 1,
            OperationRevision: 2,
            new DataRightsSubjectCoordinate(
                StaffDataRightsCoordinates.Owner,
                StaffDataRightsCoordinates.StaffMemberRecordType,
                StaffId,
                RecordVersion: 2),
            ApprovalEvidence: null!,
            "user:operator",
            Now.AddMinutes(2));

    private static DataRightsAnonymisationRestoreRequestV3
        CreateRestoreRequest() =>
        new(
            DataRightsAnonymisationRestoreContractV3.CurrentVersion,
            ScopeId,
            Guid.NewGuid(),
            TenantSequence: 1,
            new string('a', 64),
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            RoutingPropertyId: null,
            StaffDataRightsCoordinates.Owner,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            StaffId,
            OwnerReceiptContractVersion: 1,
            Guid.NewGuid(),
            new string('b', 64),
            ResultingRecordVersion: 3,
            Now);

    private static DataRightsAnonymisationContributionRequestV2
        CreateWorkspaceAnonymisationRequest(
            Guid anchorProcessId,
            long selectedAnchorVersion) =>
        new(
            DataRightsAnonymisationContractV2.CurrentVersion,
            ScopeId,
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            PropertyId: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalRevision: 1,
            OperationRevision: 2,
            new DataRightsSubjectCoordinate(
                WorkspacesDataRightsCoordinates.Owner,
                WorkspacesDataRightsCoordinates
                    .StaffAccessProcessRecordType,
                anchorProcessId,
                selectedAnchorVersion),
            ApprovalEvidence: null!,
            "user:operator",
            Now.AddMinutes(2));

    private static DataRightsAnonymisationRestoreRequestV3
        CreateWorkspaceRestoreRequest(
            WorkspaceStaffCorrelationAnonymisationReceipt receipt,
            string? ownerReceiptSha256 = null) =>
        new(
            DataRightsAnonymisationRestoreContractV3.CurrentVersion,
            ScopeId,
            Guid.NewGuid(),
            TenantSequence: 1,
            new string('d', 64),
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            RoutingPropertyId: null,
            WorkspacesDataRightsCoordinates.Owner,
            WorkspacesDataRightsCoordinates
                .StaffAccessProcessRecordType,
            receipt.AnchorProcessId,
            receipt.ContractVersion,
            receipt.Id,
            ownerReceiptSha256 ?? receipt.CanonicalSha256,
            receipt.ResultingAnchorVersion,
            receipt.CompletedAtUtc);

    private static WorkspaceStaffCorrelationAnonymisationReceipt
        CreateWorkspaceCorrelationReceipt() =>
        WorkspaceStaffCorrelationAnonymisationReceipt.Create(
            Guid.NewGuid(),
            ScopeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 4,
            operationRevision: 5,
            Guid.NewGuid(),
            StaffId,
            selectedStaffVersion: 2,
            selectedAnchorVersion: 4,
            resultingAnchorVersion: 5,
            onboardingRecordsScrubbed: 1,
            accessProcessRecordsScrubbed: 1,
            accessPlanRecordsScrubbed: 0,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "user:privacy-executor",
            Now).Value;

    private static WorkspaceStaffCorrelationAnonymisationSnapshot
        CreateAnonymisedWorkspaceSnapshot(
            WorkspaceStaffCorrelationAnonymisationReceipt receipt) =>
        new(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Eligible,
            receipt.AnchorProcessId,
            receipt.ResultingAnchorVersion,
            receipt.StaffMemberId,
            receipt.SelectedStaffVersion,
            receipt.CreateSubjectPseudonym(),
            receipt.ResultingStateSha256,
            receipt.OnboardingRecordsScrubbed,
            receipt.AccessProcessRecordsScrubbed,
            receipt.AccessPlanRecordsScrubbed);

    private static StaffRetentionAnonymisationPrerequisiteRequest
        CreateRetentionRequest() =>
        new(
            StaffRetentionAnonymisationPrerequisiteContract
                .CurrentVersion,
            Guid.NewGuid(),
            ScopeId,
            StaffId,
            SelectedStaffVersion: 2);

    private static WorkspaceStaffAccessProcess CreateProcess(
        WorkspaceStaffAccessTargetState targetState,
        long targetVersion,
        IReadOnlyCollection<Guid> profiles) => WorkspaceStaffAccessProcess.Create(
        Guid.NewGuid(),
        ScopeId,
        StaffId,
        "member-a",
        targetState,
        targetVersion,
        new DateOnly(2026, 7, 21),
        "user:owner",
        profiles.Select(profileId => new WorkspaceStaffAccessProfileTarget(
            profileId,
            WorkspaceAccessScopes.Create(ScopeId).Value)).ToArray(),
        Now).Value;

    private sealed class FakeProcessRepository(params WorkspaceStaffAccessProcess[] processes)
        : IWorkspaceStaffAccessProcessRepository
    {
        public bool FailLatestSuspensionRead { get; init; }
        public List<WorkspaceStaffAccessProcess> Processes { get; } = [.. processes];

        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Processes.SingleOrDefault(process => process.Id == processId));

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) => Task.FromResult(this.Processes.SingleOrDefault(
            process => process.StaffMemberId == staffMemberId &&
                process.TargetStaffVersion == targetStaffVersion));

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) => Task.FromResult(this.Processes.SingleOrDefault(
            process => process.StaffMemberId == staffMemberId &&
                process.State != WorkspaceStaffAccessProcessState.Completed));

        public Task<WorkspaceStaffAccessProcess?> GetLatestCompletedSuspensionAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            if (this.FailLatestSuspensionRead)
            {
                throw new InvalidOperationException("The persistence read must not be used for an observed commit.");
            }

            return Task.FromResult(this.Processes
                .Where(process => process.StaffMemberId == staffMemberId &&
                    process.SubjectId == subjectId &&
                    process.TargetState == WorkspaceStaffAccessTargetState.Suspended &&
                    process.State == WorkspaceStaffAccessProcessState.Completed)
                .OrderByDescending(process => process.TargetStaffVersion)
                .FirstOrDefault());
        }

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Processes.SingleOrDefault(process =>
                process.StaffMemberId == staffMemberId &&
                process.TargetStaffVersion == targetStaffVersion &&
                process.TargetState ==
                    WorkspaceStaffAccessTargetState.Departed &&
                process.State ==
                    WorkspaceStaffAccessProcessState.Completed));

        public Task<WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) => Task.FromResult(
            new WorkspaceStaffAccessProcessListResponse(
                [],
                page.Page,
                page.PageSize,
                HasMore: false));

        public Task AddAsync(
            WorkspaceStaffAccessProcess process,
            CancellationToken cancellationToken)
        {
            this.Processes.Add(process);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestorationSuppressionReader(
        WorkspaceStaffOnboardingRestorationSuppressionState state =
            WorkspaceStaffOnboardingRestorationSuppressionState.None)
        : IWorkspaceStaffOnboardingRestorationSuppressionReader
    {
        public int ReadCount { get; private set; }

        public Task<WorkspaceStaffOnboardingRestorationSuppressionState> ReadAsync(
            string scopeId,
            Guid staffMemberId,
            string authSubjectId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ScopeId, scopeId);
            Assert.Equal(StaffId, staffMemberId);
            Assert.Equal("member-a", authSubjectId);
            this.ReadCount++;
            return Task.FromResult(state);
        }
    }

    private sealed class FakeMembershipLifecycle(
        List<string> operations,
        OrganizationMembershipLifecycleOutcome outcome = OrganizationMembershipLifecycleOutcome.Changed)
        : IOrganizationMembershipLifecycle
    {
        public Task<OrganizationMembershipLifecycleResult> EnsureStateAsync(
            Guid organizationId,
            string subjectId,
            OrganizationMembershipStatus desiredStatus,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"membership:{desiredStatus}");
            return Task.FromResult(new OrganizationMembershipLifecycleResult(outcome, null));
        }
    }

    private sealed class FakeStaffRestoreStateReader(
        StaffAnonymisationRestoreState? state)
        : IStaffAnonymisationRestoreStateReader
    {
        public Task<StaffAnonymisationRestoreState?> ReadAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                string.Equals(
                    tenantId,
                    ScopeId,
                    StringComparison.Ordinal) &&
                staffMemberId == StaffId
                    ? state
                    : null);
    }

    private sealed class FakeCorrelationRepository
        : IWorkspaceStaffRetentionCorrelationRepository
    {
        public WorkspaceStaffRetentionCorrelationReceipt? Receipt
        {
            get;
            private set;
        }

        public int ScrubCount { get; private set; }

        public int RequestCount { get; private set; }

        public void SeedReceipt(
            WorkspaceStaffRetentionCorrelationReceipt receipt) =>
            this.Receipt = receipt;

        public Task<WorkspaceStaffRetentionCorrelationReceipt?>
            GetAsync(
                Guid staffMemberId,
                long selectedStaffVersion,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Receipt is not null &&
                this.Receipt.StaffMemberId == staffMemberId &&
                this.Receipt.SelectedStaffVersion ==
                    selectedStaffVersion
                    ? this.Receipt
                    : null);

        public Task<Result<
            WorkspaceStaffRetentionCorrelationReceipt>> ScrubAsync(
                WorkspaceStaffRetentionCorrelationScrubRequest request,
                CancellationToken cancellationToken)
        {
            this.RequestCount++;
            if (this.Receipt is not null)
            {
                return Task.FromResult(this.Receipt.Matches(
                        request.TenantId,
                        request.StaffMemberId,
                        request.SelectedStaffVersion)
                    ? Result.Success(this.Receipt)
                    : Result.Failure<
                        WorkspaceStaffRetentionCorrelationReceipt>(
                        WorkspaceStaffRetentionErrors.ReceiptInvalid));
            }

            this.ScrubCount++;
            Result<WorkspaceStaffRetentionCorrelationReceipt>
                created =
                WorkspaceStaffRetentionCorrelationReceipt.Create(
                    request.ReceiptId,
                    request.TenantId,
                    request.ExecutionId,
                    request.StaffMemberId,
                    request.SelectedStaffVersion,
                    onboardingRecordsScrubbed: 1,
                    accessProcessRecordsScrubbed: 1,
                    accessPlanRecordsScrubbed: 1,
                    request.CompletedAtUtc);
            if (created.IsSuccess)
            {
                this.Receipt = created.Value;
            }

            return Task.FromResult(created);
        }
    }

    private sealed class NoDataRightsCorrelationRepository(
        WorkspaceStaffCorrelationAnonymisationTombstone?
            tombstone = null,
        WorkspaceStaffCorrelationAnonymisationSnapshot?
            snapshot = null,
        WorkspaceStaffCorrelationAnonymisationSnapshot?
            anonymisedSnapshot = null)
        : IWorkspaceStaffCorrelationAnonymisationRepository
    {
        public Task<WorkspaceStaffCorrelationAnonymisationSnapshot>
            ResolveAsync(
                string tenantId,
                Guid staffMemberId,
                long selectedStaffVersion,
                string? subjectId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                snapshot ??
                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        .Unavailable());

        public Task<WorkspaceStaffCorrelationAnonymisationSnapshot>
            ReadAsync(
                string tenantId,
                Guid anchorProcessId,
                long selectedAnchorVersion,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                snapshot ??
                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        .Unavailable());

        public Task<WorkspaceStaffCorrelationAnonymisationSnapshot>
            ReadAnonymisedAsync(
                string tenantId,
                Guid anchorProcessId,
                long resultingAnchorVersion,
                Guid ownerReceiptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                anonymisedSnapshot ??
                    snapshot ??
                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        .Unavailable());

        public Task<WorkspaceStaffCorrelationAnonymisationReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult<
                WorkspaceStaffCorrelationAnonymisationReceipt?>(null);

        public Task<
            WorkspaceStaffCorrelationAnonymisationTombstone?>
            GetTombstoneAsync(
                Guid anchorProcessId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                tombstone?.Id == anchorProcessId
                    ? tombstone
                    : null);

        public Task<
            WorkspaceStaffCorrelationAnonymisationTombstone?>
            FindTombstoneAsync(
                string tenantId,
                Guid staffMemberId,
                long selectedStaffVersion,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                tombstone?.StaffMemberId == staffMemberId &&
                tombstone.SelectedStaffVersion ==
                    selectedStaffVersion
                    ? tombstone
                    : null);

        public Task<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt?>
            GetRestoreReceiptAsync(
                Guid ledgerEntryId,
                CancellationToken cancellationToken) =>
            Task.FromResult<
                WorkspaceStaffCorrelationAnonymisationRestoreReceipt?>(
                null);

        public Task<Result<
            WorkspaceStaffCorrelationAnonymisationReceipt>>
            ApplyAsync(
                WorkspaceStaffCorrelationAnonymisationApplyRequest
                    request,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>>
            RestoreAsync(
                WorkspaceStaffCorrelationAnonymisationRestoreRequest
                    request,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRequestDispatcher(
        FakeCorrelationRepository correlations,
        IWorkspaceStaffRetentionAccessClosure accessClosure)
        : IRequestDispatcher
    {
        public async Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            if (command is not
                ScrubWorkspaceStaffRetentionCorrelationCommand scrub)
            {
                throw new InvalidOperationException(
                    $"Unexpected command '{command.GetType().Name}'.");
            }

            WorkspaceStaffAccessClosureResult closure =
                await accessClosure.EnsureClosedAsync(
                    scrub.TenantId,
                    scrub.StaffMemberId,
                    scrub.SelectedStaffVersion,
                    cancellationToken);
            if (closure.Status !=
                WorkspaceStaffAccessClosureStatus.Completed)
            {
                return Result.Failure<TResponse>(
                    new(closure.Code, closure.Code));
            }

            Result<WorkspaceStaffRetentionCorrelationReceipt> result =
                await correlations.ScrubAsync(
                    new WorkspaceStaffRetentionCorrelationScrubRequest(
                        CorrelationReceiptId,
                        scrub.ExecutionId,
                        scrub.TenantId,
                        scrub.StaffMemberId,
                        scrub.SelectedStaffVersion,
                        closure.SubjectId,
                        Now),
                    cancellationToken);
            return (Result<TResponse>)(object)result;
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedRequestDispatcher : IRequestDispatcher
    {
        public int SendCount { get; private set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.SendCount++;
            throw new InvalidOperationException(
                $"Unexpected command '{command.GetType().Name}'.");
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                $"Unexpected query '{query.GetType().Name}'.");
    }

    private sealed class FakeRoles(List<string> operations) : IAccessControlRoleProvisioner
    {
        private readonly HashSet<(AccessSubjectKind Kind, string Subject, string Role, string Scope)> assignments = [];

        public void Add(AccessSubject subject, string role, AccessScope scope) =>
            this.assignments.Add((subject.Kind, subject.Id, role, scope.Value));

        public bool Has(AccessSubject subject, string role, AccessScope scope) =>
            this.assignments.Contains((subject.Kind, subject.Id, role, scope.Value));

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.Add(subject, roleName, scope);
            operations.Add($"role:assign:{roleName}");
            return Task.CompletedTask;
        }

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            bool removed = this.assignments.Remove((subject.Kind, subject.Id, roleName, scope.Value));
            operations.Add($"role:remove:{roleName}");
            return Task.FromResult(removed
                ? AccessControlAssignmentRemovalOutcome.Removed
                : AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Has(subject, roleName, scope));

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new AccessControlPage<AccessControlRoleAssignment>([], page, pageSize, false));
    }

    private sealed class FakeProfiles(List<string> operations)
        : IAccessProfileProvisioner, IScopedAccessProfileProvisioner
    {
        private readonly Dictionary<Guid, AccessProfileDto> profiles = [];
        private readonly Dictionary<(AccessSubjectKind Kind, string Subject, string Scope), HashSet<Guid>> assignments = [];

        public AccessProfileDto AddProfile(AccessScope scope, string key)
        {
            AccessProfileDto profile = new(
                Guid.NewGuid(), scope.Value, key, key, string.Empty, AccessProfileStatus.Active,
                1, [], 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            this.profiles[profile.Id] = profile;
            return profile;
        }

        public void Assign(AccessSubject subject, AccessScope scope, Guid profileId) =>
            this.GetAssignments(subject, scope).Add(profileId);

        public Guid[] AssignedProfileIds(AccessSubject subject, AccessScope scope) =>
            this.GetAssignments(subject, scope).ToArray();

        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            AccessProfileDto? existing = this.profiles.Values.SingleOrDefault(
                profile => profile.OwnerScope == ownerScope.Value && profile.Key == definition.Key);
            return Task.FromResult(existing ?? this.AddProfile(ownerScope, definition.Key));
        }

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) => Task.FromResult(this.profiles.Values
            .SingleOrDefault(profile => profile.OwnerScope == ownerScope.Value && profile.Key == key));

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            HashSet<Guid> ids = this.GetAssignments(subject, ownerScope);
            return Task.FromResult(new AccessProfileAssignmentSet(
                subject,
                ownerScope,
                this.profiles.Values.Where(profile => ids.Contains(profile.Id)).ToArray()));
        }

        public Task<AccessProfileAssignmentReconciliation> ReconcileSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            IReadOnlyCollection<Guid> profileIds,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            operations.Add("profiles:reconcile");
            HashSet<Guid> current = this.GetAssignments(subject, ownerScope);
            int added = profileIds.Count(profileId => !current.Contains(profileId));
            int removed = current.Count(profileId => !profileIds.Contains(profileId));
            current.Clear();
            current.UnionWith(profileIds);
            return Task.FromResult(new AccessProfileAssignmentReconciliation(
                subject, ownerScope, profileIds.ToArray(), added, removed));
        }

        public Task<ScopedAccessProfileAssignmentSet> GetSubjectScopedAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            HashSet<Guid> ids = this.GetAssignments(subject, ownerScope);
            ScopedAccessProfileAssignment[] current = this.profiles.Values
                .Where(profile => ids.Contains(profile.Id))
                .Select(profile => new ScopedAccessProfileAssignment(profile, ownerScope))
                .ToArray();
            return Task.FromResult(new ScopedAccessProfileAssignmentSet(subject, ownerScope, current));
        }

        public async Task<ScopedAccessProfileAssignmentReconciliation>
            ReconcileSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default)
        {
            AccessProfileAssignmentReconciliation result = await this.ReconcileSubjectAssignmentsAsync(
                subject,
                ownerScope,
                targets.Select(target => target.ProfileId).ToArray(),
                actor,
                cancellationToken);
            return new ScopedAccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                targets.ToArray(),
                result.AssignedCount,
                result.UnassignedCount);
        }

        private HashSet<Guid> GetAssignments(AccessSubject subject, AccessScope scope)
        {
            var key = (subject.Kind, subject.Id, scope.Value);
            if (!this.assignments.TryGetValue(key, out HashSet<Guid>? current))
            {
                current = [];
                this.assignments[key] = current;
            }

            return current;
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
