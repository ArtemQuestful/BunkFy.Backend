namespace BunkFy.Modules.Workspaces.Application.Models;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Domain;

public sealed record WorkspaceStaffIdentityAnchorSweepCandidate(
    Guid ApplicationId,
    long IdentityAnchorSweepOrdinal,
    string SubjectId,
    bool HasLocalAnchorState);

public sealed record WorkspaceStaffIdentityAnchorSweepPage(
    Guid CheckpointId,
    long CheckpointVersion,
    Guid CycleId,
    long? UpperOrdinal,
    long? ExpectedAfterOrdinal,
    long? NextAfterOrdinal,
    bool ReachedEnd,
    bool AdvanceRequired,
    IReadOnlyList<WorkspaceStaffIdentityAnchorSweepCandidate> Candidates);

public sealed record WorkspaceStaffIdentityAnchorSweepAdvance(
    Guid CheckpointId,
    long ExpectedCheckpointVersion,
    Guid ExpectedCycleId,
    long? ExpectedAfterOrdinal,
    long NextAfterOrdinal,
    bool ReachedEnd,
    Guid AdvanceId,
    Guid RunId,
    WorkspaceStaffIdentityAnchorSweepPageCounts Counts);

public enum WorkspaceStaffIdentityAnchorSweepCandidateOutcome
{
    Unknown = 0,
    NoAnchor = 1,
    Removed = 2,
    PassOneCommitted = 3,
    ResolutionReadyToRecord = 4,
    ObservedNow = 5,
    AlreadyObserved = 6
}

public sealed record WorkspaceStaffIdentityAnchorSweepCandidateResult(
    Guid ApplicationId,
    WorkspaceStaffIdentityAnchorSweepCandidateOutcome Outcome,
    StaffWorkspaceOnboardingIdentityAnchorResolutionRequest?
        ResolutionRequest = null);

/// <remarks>
/// A completed bounded cycle is not a point-in-time database snapshot.
/// Store-generated ordinals are ordered when allocated, so release assurance
/// still requires a stable-universe barrier that excludes late commits.
/// </remarks>
public sealed record WorkspaceStaffIdentityAnchorSweepStatus(
    string ScopeId,
    bool HasCheckpoint,
    int ProtocolVersion,
    long CheckpointVersion,
    bool HasActiveCycle,
    Guid? CycleId,
    long? CycleUpperOrdinal,
    long? AfterOrdinal,
    DateTimeOffset? CycleStartedAtUtc,
    WorkspaceStaffIdentityAnchorSweepPageCounts CurrentCycle,
    Guid? LastCompletedCycleId,
    long? LastCompletedUpperOrdinal,
    DateTimeOffset? LastCompletedAtUtc,
    WorkspaceStaffIdentityAnchorSweepPageCounts LastCompletedCycle,
    Guid? LastRunId,
    DateTimeOffset? UpdatedAtUtc,
    bool HasCompletedBoundedCycle)
{
    public bool HasCompletedCycleObservation =>
        this.LastCompletedAtUtc.HasValue;

    public long? LastCompletedObservedBacklogCount =>
        this.HasCompletedCycleObservation
            ? this.LastCompletedCycle.BacklogCount
            : null;
}
