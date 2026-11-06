namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Modules.AccessControl.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingIdentityAnchorConvergenceTests
{
    [Fact]
    public async Task Absent_anchor_preserves_applicant_authority_without_mutation()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        long version = application.Version;
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Absent(application));

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Absent,
            result.Value.Outcome);
        Assert.Equal(version, application.Version);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Exact_active_anchor_binds_and_redacts_once()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Unresolved(
                application,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact));

        var first = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);
        long convergedVersion = application.Version;
        var replay = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ConvergedNow,
            first.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active,
            replay.Value.Outcome);
        Assert.Equal(StaffMemberId, application.StaffMemberId);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Equal(convergedVersion, application.Version);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public async Task Exact_suspended_anchor_denies_and_terminalizes_without_resurrection()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended;
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader reader = new(
            request => Unresolved(
                application,
                lifecycle,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact));
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(reader, roles, profiles);

        var first = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);
        long resolvedVersion = application.Version;
        var replay = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);
        lifecycle = StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
        var laterActive = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.True(laterActive.IsSuccess, laterActive.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            first.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            replay.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            laterActive.Value.Outcome);
        Assert.Equal(resolvedVersion, application.Version);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Equal(1, profiles.DenyCount);
        Assert.Equal(2, roles.RemovalCount);
        AssertApplicantDataRedacted(application);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing)]
    public async Task Terminal_target_denies_access_then_records_resolution_intent(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(application, lifecycle, subjectMatch),
                roles,
                profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            result.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(
            ResolutionEventId,
            application.IdentityAnchorResolutionEventId);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Equal(1, profiles.DenyCount);
        Assert.Equal(2, roles.RemovalCount);
        AssertApplicantDataRedacted(application);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Missing,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    public async Task Mismatched_or_missing_target_fails_closed_without_redaction(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        long version = application.Version;
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    lifecycle,
                    subjectMatch),
                roles,
                profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.IdentityAnchorConflict,
            result.Error);
        Assert.Equal(version, application.Version);
        Assert.Null(application.StaffMemberId);
        Assert.NotNull(application.DisplayName);
        Assert.Equal(0, profiles.DenyCount);
        Assert.Equal(0, roles.RemovalCount);
    }

    [Theory]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active)]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended)]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed)]
    public async Task Historical_subject_mismatch_denies_old_subject_and_supersedes(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    lifecycle,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                        .Mismatch),
                roles,
                profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            result.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Equal(application.SubjectId, Assert.Single(profiles.DeniedSubjects));
        Assert.Equal(2, roles.RemovalCount);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public async Task Historical_completed_subject_mismatch_is_forced_to_superseded()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        SetStatusForLegacyMaterialization(
            application,
            WorkspaceStaffOnboardingState.Completed);
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                        .Mismatch),
                profiles: profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted,
            application.IdentityAnchorResolutionDisposition);
        Assert.Equal(application.SubjectId, Assert.Single(profiles.DeniedSubjects));
    }

    [Theory]
    [InlineData(
        WorkspaceStaffOnboardingState.Rejected,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            .RejectedRedacted)]
    [InlineData(
        WorkspaceStaffOnboardingState.Superseded,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            .SupersededRedacted)]
    [InlineData(
        WorkspaceStaffOnboardingState.Expired,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            .ExpiredRedacted)]
    [InlineData(
        WorkspaceStaffOnboardingState.Withdrawn,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            .WithdrawnRedacted)]
    public async Task Historical_mismatch_preserves_negative_terminal_truth(
        WorkspaceStaffOnboardingState state,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            expectedDisposition)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        SetStatusForLegacyMaterialization(application, state);
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                        .Mismatch),
                profiles: profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(state, application.Status);
        Assert.Equal(
            expectedDisposition,
            application.IdentityAnchorResolutionDisposition);
        Assert.Equal(application.SubjectId, Assert.Single(profiles.DeniedSubjects));
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public async Task Exact_resolved_outcome_observes_once_without_changing_captured_version()
    {
        WorkspaceStaffOnboarding application = CreateCompletedApplication();
        long capturedVersion =
            application.IdentityAnchorResolutionApplicationVersion!.Value;
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Resolved(application, capturedVersion));

        var first = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);
        long observedVersion = application.Version;
        var replay = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionObserved,
            first.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            replay.Value.Outcome);
        Assert.Equal(capturedVersion + 1, observedVersion);
        Assert.Equal(observedVersion, application.Version);
        Assert.Equal(
            capturedVersion,
            application.IdentityAnchorResolutionApplicationVersion);
    }

    [Fact]
    public async Task Resolved_outcome_with_another_version_is_a_conflict()
    {
        WorkspaceStaffOnboarding application = CreateCompletedApplication();
        long version = application.Version;
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Resolved(application, version + 1));

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(version, application.Version);
        Assert.Null(application.IdentityAnchorResolutionObservedAtUtc);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
        0,
        0)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended,
        1,
        2)]
    public async Task Completed_live_target_records_resolution_without_removing_access(
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle,
        int expectedProfileDenials,
        int expectedRoleRemovals)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            StaffMemberId,
            ResolutionEventId,
            ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        application.ClearDomainEvents();
        SetStatusForLegacyMaterialization(
            application,
            WorkspaceStaffOnboardingState.Completed);
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    lifecycle,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact),
                roles,
                profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            result.Value.Outcome);
        Assert.Equal(expectedProfileDenials, profiles.DenyCount);
        Assert.Equal(expectedRoleRemovals, roles.RemovalCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            application.IdentityAnchorResolutionDisposition);
    }

    [Fact]
    public async Task Expected_event_target_must_match_the_authoritative_anchor()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Unresolved(
                application,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact));
        long version = application.Version;

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None,
            Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(version, application.Version);
        Assert.Null(application.StaffMemberId);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Expected_event_target_rejects_an_absent_authoritative_anchor()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(Absent(application));
        long version = application.Version;

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None,
            StaffMemberId);

        Assert.True(result.IsFailure);
        Assert.Equal(version, application.Version);
        Assert.Null(application.StaffMemberId);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Open_suspension_process_blocks_without_precommit_resolution()
    {
        WorkspaceStaffOnboarding application = CreateStaffReadyApplication();
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        List<string> calls = [];
        SingleOpenProcessRepository accessProcesses = new(
            CreateAccessProcess(
                application,
                WorkspaceStaffAccessTargetState.Suspended),
            calls);
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                    request => Unresolved(
                        application,
                        lifecycle,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Exact)),
                roles,
                profiles,
                accessProcesses,
                calls);

        var candidate = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);
        Assert.True(candidate.IsSuccess, candidate.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active,
            candidate.Value.Outcome);
        Assert.Empty(calls);

        var result = await convergence.FenceActiveGrantAcquiredAsync(
            application,
            StaffMemberId,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .IdentityAnchorLifecycleTransitionPending,
            result.Error);
        Assert.Equal(["staff-coordinate", "open-process"], calls);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Null(application.IdentityAnchorResolutionEventId);
        Assert.Equal(0, profiles.DenyCount);
        Assert.Equal(0, roles.RemovalCount);

        accessProcesses.OpenProcess = null;
        calls.Clear();
        var cleared = await convergence.FenceActiveGrantAcquiredAsync(
            application,
            StaffMemberId,
            CancellationToken.None);

        Assert.True(cleared.IsSuccess, cleared.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active,
            cleared.Value.Outcome);
        Assert.Null(application.IdentityAnchorResolutionEventId);

        lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended;
        var committedSuspension = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(committedSuspension.IsSuccess, committedSuspension.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            committedSuspension.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(1, profiles.DenyCount);
        Assert.Equal(2, roles.RemovalCount);
    }

    [Theory]
    [InlineData(WorkspaceStaffAccessTargetState.Active)]
    [InlineData(WorkspaceStaffAccessTargetState.Suspended)]
    [InlineData(WorkspaceStaffAccessTargetState.Departed)]
    public async Task Open_lifecycle_process_blocks_suspended_outcome_until_authority_is_reread(
        WorkspaceStaffAccessTargetState openTargetState)
    {
        WorkspaceStaffOnboarding application = CreateStaffReadyApplication();
        long version = application.Version;
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended;
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        SingleOpenProcessRepository accessProcesses = new(
            CreateAccessProcess(
                application,
                openTargetState));
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                    request => Unresolved(
                        application,
                        lifecycle,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Exact)),
                roles,
                profiles,
                accessProcesses);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .IdentityAnchorLifecycleTransitionPending,
            result.Error);
        Assert.Equal(version, application.Version);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Null(application.IdentityAnchorResolutionEventId);
        Assert.Equal(0, profiles.DenyCount);
        Assert.Equal(0, roles.RemovalCount);

        accessProcesses.OpenProcess = null;
        lifecycle = StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
        var abortedResume = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(abortedResume.IsSuccess, abortedResume.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active,
            abortedResume.Value.Outcome);
        Assert.Equal(version, application.Version);
        Assert.Null(application.IdentityAnchorResolutionEventId);

        lifecycle =
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended;
        var committedSuspension = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(committedSuspension.IsSuccess, committedSuspension.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending,
            committedSuspension.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        Assert.Equal(1, profiles.DenyCount);
        Assert.Equal(2, roles.RemovalCount);
    }

    [Fact]
    public async Task Historical_mismatch_waits_for_open_current_subject_process()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        long version = application.Version;
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        SingleOpenProcessRepository accessProcesses = new(
            CreateAccessProcess(
                application,
                WorkspaceStaffAccessTargetState.Active,
                "subject:current-owner"));
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                    request => Unresolved(
                        application,
                        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                            .Active,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Mismatch)),
                roles,
                profiles,
                accessProcesses);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .IdentityAnchorLifecycleTransitionPending,
            result.Error);
        Assert.Equal(version, application.Version);
        Assert.Null(application.StaffMemberId);
        Assert.NotNull(application.DisplayName);
        Assert.Equal(0, profiles.DenyCount);
        Assert.Equal(0, roles.RemovalCount);
    }

    [Fact]
    public async Task Rejected_exact_anchor_denies_access_and_records_rejected_disposition()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(
            claimId,
            1,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ObserveClaimRejected(
            claimId,
            2,
            Now.AddMinutes(2)).IsSuccess);
        RecordingRoles roles = new();
        RecordingScopedProfiles profiles = new();
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence =
            CreateConvergence(
                Unresolved(
                    application,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact),
                roles,
                profiles);

        var result = await convergence.ConvergeAcquiredAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, profiles.DenyCount);
        Assert.Equal(2, roles.RemovalCount);
        Assert.Equal(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .RejectedRedacted,
            application.IdentityAnchorResolutionDisposition);
    }

    private static WorkspaceStaffOnboardingIdentityAnchorConvergence
        CreateConvergence(
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome,
            RecordingRoles? roles = null,
            RecordingScopedProfiles? profiles = null)
        => CreateConvergence(
            new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                request => request.ApplicationId == outcome.ApplicationId
                    ? outcome
                    : StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                        .Absent(request)),
            roles,
            profiles);

    private static WorkspaceStaffOnboardingIdentityAnchorConvergence
        CreateConvergence(
            StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader reader,
            RecordingRoles? roles = null,
            RecordingScopedProfiles? profiles = null,
            IWorkspaceStaffAccessProcessRepository? accessProcesses = null,
            List<string>? accessLockCalls = null)
    {
        roles ??= new RecordingRoles();
        profiles ??= new RecordingScopedProfiles();
        accessProcesses ??=
            WorkspaceStaffAccessMutationTestSupport.NoOpenProcesses;
        return new WorkspaceStaffOnboardingIdentityAnchorConvergence(
            reader,
            WorkspaceStaffAccessMutationTestSupport.Create(
                accessProcesses,
                calls: accessLockCalls),
            accessProcesses,
            new WorkspaceAccessProvisioner(
                roles,
                profiles: null!,
                profiles),
            new TestClock(),
            new FixedIdGenerator(ContinuationEventId));
    }

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Absent(
        WorkspaceStaffOnboarding application) =>
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader.Absent(
            new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                application.Id,
                application.SubjectId));

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Unresolved(
        WorkspaceStaffOnboarding application,
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch) => new(
            application.Id,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
            StaffMemberId,
            lifecycle,
            subjectMatch,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null,
            ResolutionEventId);

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Resolved(
        WorkspaceStaffOnboarding application,
        long version) => new(
            application.Id,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
            StaffMemberId,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            version,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            ResolutionEventId);

    private static WorkspaceStaffOnboarding CreateApplication() =>
        WorkspaceStaffOnboardingTests.CreateApplication();

    private static WorkspaceStaffOnboarding CreateCompletedApplication()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            StaffMemberId,
            ResolutionEventId,
            ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        return application;
    }

    private static WorkspaceStaffOnboarding CreateStaffReadyApplication()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            StaffMemberId,
            ResolutionEventId,
            ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        application.ClearDomainEvents();
        return application;
    }

    private static WorkspaceStaffAccessProcess CreateAccessProcess(
        WorkspaceStaffOnboarding application,
        WorkspaceStaffAccessTargetState targetState,
        string? subjectId = null) =>
        WorkspaceStaffAccessProcess.Create(
            Guid.NewGuid(),
            application.ScopeId,
            StaffMemberId,
            subjectId ?? application.SubjectId,
            targetState,
            2,
            DateOnly.FromDateTime(Now.UtcDateTime),
            "integration:staff",
            [],
            Now).Value;

    private static void AssertApplicantDataRedacted(
        WorkspaceStaffOnboarding application)
    {
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Null(application.LegalName);
        Assert.Null(application.WorkEmail);
        Assert.Null(application.WorkPhone);
        Assert.Null(application.EmployeeNumber);
        Assert.Null(application.JobTitle);
        Assert.Null(application.Department);
    }

    private static void SetStatusForLegacyMaterialization(
        WorkspaceStaffOnboarding application,
        WorkspaceStaffOnboardingState status) =>
        typeof(WorkspaceStaffOnboarding)
            .GetProperty(
                nameof(WorkspaceStaffOnboarding.Status),
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)!
            .SetValue(application, status);

    private sealed class RecordingRoles : IAccessControlRoleProvisioner
    {
        public int RemovalCount { get; private set; }

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.RemovalCount++;
            return Task.FromResult(
                AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<AccessControlPage<AccessControlRoleAssignment>>
            ListAssignmentsAsync(
                string roleName,
                AccessScope scope,
                int page,
                int pageSize,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlPage<AccessControlRoleAssignment>(
                [],
                page,
                pageSize,
                HasMore: false));
    }

    private sealed class RecordingScopedProfiles
        : IScopedAccessProfileProvisioner
    {
        public int DenyCount { get; private set; }
        public List<string> DeniedSubjects { get; } = [];

        public Task<ScopedAccessProfileAssignmentSet>
            GetSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScopedAccessProfileAssignmentSet(
                subject,
                ownerScope,
                []));

        public Task<ScopedAccessProfileAssignmentReconciliation>
            ReconcileSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default)
        {
            Assert.Empty(targets);
            this.DenyCount++;
            this.DeniedSubjects.Add(subject.Id);
            return Task.FromResult(
                new ScopedAccessProfileAssignmentReconciliation(
                    subject,
                    ownerScope,
                    [],
                    AssignedCount: 0,
                    UnassignedCount: 0));
        }
    }

    private sealed class SingleOpenProcessRepository(
        WorkspaceStaffAccessProcess? process,
        List<string>? calls = null)
        : IWorkspaceStaffAccessProcessRepository
    {
        public WorkspaceStaffAccessProcess? OpenProcess { get; set; } = process;

        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            calls?.Add("open-process");
            return Task.FromResult(this.OpenProcess);
        }

        public Task<WorkspaceStaffAccessProcess?>
            GetLatestCompletedSuspensionAsync(
                Guid staffMemberId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<
            BunkFy.Modules.Workspaces.Contracts
                .WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessProcess candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(10);
    }

    private sealed class FixedIdGenerator(Guid value) : IIdGenerator
    {
        public Guid NewId() => value;
    }

    private static readonly Guid StaffMemberId =
        Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid ResolutionEventId =
        Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid ContinuationEventId =
        Guid.Parse("30000000-0000-0000-0000-000000000005");
    private static readonly DateTimeOffset Now =
        WorkspaceStaffOnboardingTests.Now;
}
