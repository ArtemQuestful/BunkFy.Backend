namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffAccessProcess : ScopedAggregateRoot<Guid>
{
    public const int SubjectIdMaxLength = 256;
    public const int ActorIdMaxLength = 200;
    public const int FailureCodeMaxLength = 200;

    private readonly List<WorkspaceStaffAccessProfileSnapshot> profileSnapshots = [];

    private WorkspaceStaffAccessProcess() { }
    private WorkspaceStaffAccessProcess(Guid id, string scopeId) : base(id, scopeId) { }

    public Guid StaffMemberId { get; private set; }
    public string SubjectId { get; private set; } = string.Empty;
    public WorkspaceStaffAccessTargetState TargetState { get; private set; }
    public WorkspaceStaffAccessRestorationDisposition RestorationDisposition
    {
        get;
        private set;
    }
    public long TargetStaffVersion { get; private set; }
    public DateOnly EffectiveOn { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public WorkspaceStaffAccessProcessState State { get; private set; }
    public string? FailureCode { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset LastChangedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public IReadOnlyCollection<WorkspaceStaffAccessProfileSnapshot> ProfileSnapshots =>
        this.profileSnapshots.AsReadOnly();

    public static Result<WorkspaceStaffAccessProcess> Create(
        Guid id,
        string scopeId,
        Guid staffMemberId,
        string subjectId,
        WorkspaceStaffAccessTargetState targetState,
        long targetStaffVersion,
        DateOnly effectiveOn,
        string requestedBy,
        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> profileTargets,
        DateTimeOffset nowUtc) => Create(
            id,
            scopeId,
            staffMemberId,
            subjectId,
            targetState,
            targetState == WorkspaceStaffAccessTargetState.Active
                ? WorkspaceStaffAccessRestorationDisposition.RestoreSnapshot
                : WorkspaceStaffAccessRestorationDisposition.NotApplicable,
            targetStaffVersion,
            effectiveOn,
            requestedBy,
            profileTargets,
            nowUtc);

    public static Result<WorkspaceStaffAccessProcess> Create(
        Guid id,
        string scopeId,
        Guid staffMemberId,
        string subjectId,
        WorkspaceStaffAccessTargetState targetState,
        WorkspaceStaffAccessRestorationDisposition restorationDisposition,
        long targetStaffVersion,
        DateOnly effectiveOn,
        string requestedBy,
        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> profileTargets,
        DateTimeOffset nowUtc)
    {
        string normalizedSubject = subjectId?.Trim() ?? string.Empty;
        string normalizedActor = requestedBy?.Trim() ?? string.Empty;
        if (id == Guid.Empty || staffMemberId == Guid.Empty ||
            !TenantIds.TryNormalize(scopeId, out string? normalizedScope) ||
            normalizedSubject.Length is 0 or > SubjectIdMaxLength ||
            normalizedActor.Length is 0 or > ActorIdMaxLength ||
            targetState == WorkspaceStaffAccessTargetState.Unknown || !Enum.IsDefined(targetState) ||
            !IsRestorationDispositionCoherent(
                targetState,
                restorationDisposition,
                profileTargets) ||
            targetStaffVersion < 2 || effectiveOn == default || profileTargets is null ||
            profileTargets.Any(target =>
                target is null ||
                target.ProfileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(target.AssignmentScope) ||
                target.AssignmentScope.Trim().Length >
                    WorkspaceStaffAccessProfileSnapshot.AssignmentScopeMaxLength))
        {
            return Result.Failure<WorkspaceStaffAccessProcess>(WorkspaceStaffAccessErrors.Invalid);
        }

        WorkspaceStaffAccessProcess process = new(id, normalizedScope)
        {
            StaffMemberId = staffMemberId,
            SubjectId = normalizedSubject,
            TargetState = targetState,
            RestorationDisposition = restorationDisposition,
            TargetStaffVersion = targetStaffVersion,
            EffectiveOn = effectiveOn,
            RequestedBy = normalizedActor,
            State = WorkspaceStaffAccessProcessState.Prepared,
            CreatedAtUtc = nowUtc,
            LastChangedAtUtc = nowUtc
        };
        process.profileSnapshots.AddRange(profileTargets
            .Select(target => new WorkspaceStaffAccessProfileTarget(
                target.ProfileId,
                target.AssignmentScope.Trim()))
            .Distinct()
            .OrderBy(target => target.AssignmentScope, StringComparer.Ordinal)
            .ThenBy(target => target.ProfileId)
            .Select(target => new WorkspaceStaffAccessProfileSnapshot(
                target.ProfileId,
                target.AssignmentScope)));
        return Result.Success(process);
    }

    public Result MarkAwaitingStaffCommit(DateTimeOffset nowUtc)
    {
        if (this.State == WorkspaceStaffAccessProcessState.AwaitingStaffCommit)
        {
            return Result.Success();
        }

        if (this.State != WorkspaceStaffAccessProcessState.Prepared)
        {
            return Result.Failure(WorkspaceStaffAccessErrors.StateConflict);
        }

        this.State = WorkspaceStaffAccessProcessState.AwaitingStaffCommit;
        this.Advance(nowUtc);
        return Result.Success();
    }

    public Result ObserveStaffCommit(DateTimeOffset nowUtc)
    {
        if (this.State is WorkspaceStaffAccessProcessState.RestorationPending or
            WorkspaceStaffAccessProcessState.Completed)
        {
            return Result.Success();
        }

        if (this.State != WorkspaceStaffAccessProcessState.AwaitingStaffCommit)
        {
            return Result.Failure(WorkspaceStaffAccessErrors.StateConflict);
        }

        if (this.TargetState == WorkspaceStaffAccessTargetState.Active)
        {
            if (this.RestorationDisposition ==
                WorkspaceStaffAccessRestorationDisposition.Suppressed)
            {
                return this.Complete(nowUtc);
            }

            this.State = WorkspaceStaffAccessProcessState.RestorationPending;
            this.Advance(nowUtc);
            return Result.Success();
        }

        return this.Complete(nowUtc);
    }

    public Result Complete(DateTimeOffset nowUtc)
    {
        if (this.State == WorkspaceStaffAccessProcessState.Completed)
        {
            return Result.Success();
        }

        if (this.State is not (WorkspaceStaffAccessProcessState.AwaitingStaffCommit or
            WorkspaceStaffAccessProcessState.RestorationPending))
        {
            return Result.Failure(WorkspaceStaffAccessErrors.StateConflict);
        }

        this.State = WorkspaceStaffAccessProcessState.Completed;
        this.FailureCode = null;
        this.CompletedAtUtc = nowUtc;
        this.Advance(nowUtc);
        return Result.Success();
    }

    public Result RecordFailure(string failureCode, DateTimeOffset nowUtc)
    {
        string normalized = failureCode?.Trim() ?? string.Empty;
        if (this.State == WorkspaceStaffAccessProcessState.Completed ||
            normalized.Length is 0 or > FailureCodeMaxLength)
        {
            return Result.Failure(WorkspaceStaffAccessErrors.StateConflict);
        }

        this.FailureCode = normalized;
        this.Advance(nowUtc);
        return Result.Success();
    }

    public bool Matches(
        string subjectId,
        WorkspaceStaffAccessTargetState targetState,
        DateOnly effectiveOn) =>
        string.Equals(this.SubjectId, subjectId.Trim(), StringComparison.Ordinal) &&
        this.TargetState == targetState &&
        this.EffectiveOn == effectiveOn;

    private void Advance(DateTimeOffset nowUtc)
    {
        this.Version++;
        this.LastChangedAtUtc = nowUtc;
    }

    private static bool IsRestorationDispositionCoherent(
        WorkspaceStaffAccessTargetState targetState,
        WorkspaceStaffAccessRestorationDisposition restorationDisposition,
        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget>? profileTargets) =>
        targetState switch
        {
            WorkspaceStaffAccessTargetState.Active =>
                restorationDisposition ==
                    WorkspaceStaffAccessRestorationDisposition.RestoreSnapshot ||
                (restorationDisposition ==
                    WorkspaceStaffAccessRestorationDisposition.Suppressed &&
                    profileTargets is { Count: 0 }),
            WorkspaceStaffAccessTargetState.Suspended or
                WorkspaceStaffAccessTargetState.Departed =>
                restorationDisposition ==
                    WorkspaceStaffAccessRestorationDisposition.NotApplicable,
            _ => false
        };
}
