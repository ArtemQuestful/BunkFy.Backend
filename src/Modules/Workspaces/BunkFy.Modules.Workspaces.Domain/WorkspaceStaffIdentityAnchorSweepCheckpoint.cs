namespace BunkFy.Modules.Workspaces.Domain;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffIdentityAnchorSweepCheckpoint
    : ScopedAggregateRoot<Guid>
{
    public const int CurrentProtocolVersion = 1;
    public const int AdvanceSha256Length = 64;

    private WorkspaceStaffIdentityAnchorSweepCheckpoint() { }

    private WorkspaceStaffIdentityAnchorSweepCheckpoint(
        Guid id,
        string scopeId)
        : base(id, scopeId)
    {
    }

    public int ProtocolVersion { get; private set; }
    public Guid? CycleId { get; private set; }
    public long? CycleUpperOrdinal { get; private set; }
    public long? AfterOrdinal { get; private set; }
    public DateTimeOffset? CycleStartedAtUtc { get; private set; }
    public long CycleScannedCount { get; private set; }
    public long CycleNoAnchorCount { get; private set; }
    public long CycleRemovedCount { get; private set; }
    public long CycleObservedCount { get; private set; }
    public long CycleAlreadyObservedCount { get; private set; }
    public long CycleDeferredCount { get; private set; }
    public long CycleConflictCount { get; private set; }
    public long CyclePassOneCommittedCount { get; private set; }
    public long CycleResolutionRecordConfirmedCount { get; private set; }
    public Guid? LastCompletedCycleId { get; private set; }
    public long? LastCompletedUpperOrdinal { get; private set; }
    public DateTimeOffset? LastCompletedAtUtc { get; private set; }
    public long LastCompletedScannedCount { get; private set; }
    public long LastCompletedNoAnchorCount { get; private set; }
    public long LastCompletedRemovedCount { get; private set; }
    public long LastCompletedObservedCount { get; private set; }
    public long LastCompletedAlreadyObservedCount { get; private set; }
    public long LastCompletedDeferredCount { get; private set; }
    public long LastCompletedConflictCount { get; private set; }
    public long LastCompletedPassOneCommittedCount { get; private set; }
    public long LastCompletedResolutionRecordConfirmedCount { get; private set; }
    public Guid? LastAdvanceId { get; private set; }
    public string? LastAdvanceSha256 { get; private set; }
    public Guid? LastRunId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public bool HasActiveCycle => this.CycleId.HasValue;

    public static Result<WorkspaceStaffIdentityAnchorSweepCheckpoint> Create(
        Guid id,
        string tenantId,
        DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            nowUtc == default)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorSweepCheckpoint>(
                WorkspaceStaffIdentityAnchorSweepErrors.Invalid);
        }

        return Result.Success(
            new WorkspaceStaffIdentityAnchorSweepCheckpoint(id, scopeId)
            {
                ProtocolVersion = CurrentProtocolVersion,
                UpdatedAtUtc = nowUtc.ToUniversalTime()
            });
    }

    public Result BeginCycle(
        Guid cycleId,
        long upperOrdinal,
        Guid runId,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
        if (cycleId == Guid.Empty ||
            upperOrdinal <= 0 ||
            runId == Guid.Empty ||
            this.HasActiveCycle ||
            normalizedNow < this.UpdatedAtUtc)
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorSweepErrors.CheckpointConflict);
        }

        this.CycleId = cycleId;
        this.CycleUpperOrdinal = upperOrdinal;
        this.AfterOrdinal = null;
        this.CycleStartedAtUtc = normalizedNow;
        this.ResetCycleCounts();
        this.LastRunId = runId;
        this.UpdatedAtUtc = normalizedNow;
        this.Version++;
        return Result.Success();
    }

    public Result CompleteEmptyCycle(
        Guid cycleId,
        Guid advanceId,
        Guid runId,
        DateTimeOffset nowUtc)
    {
        string digest = CreateEmptyAdvanceDigest(cycleId, runId);
        if (this.LastAdvanceId == advanceId)
        {
            return !this.HasActiveCycle &&
                this.LastAdvanceSha256 == digest
                ? Result.Success()
                : Result.Failure(
                    WorkspaceStaffIdentityAnchorSweepErrors
                        .CheckpointConflict);
        }

        DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
        if (cycleId == Guid.Empty ||
            advanceId == Guid.Empty ||
            runId == Guid.Empty ||
            this.HasActiveCycle ||
            normalizedNow < this.UpdatedAtUtc)
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorSweepErrors.CheckpointConflict);
        }

        this.LastCompletedCycleId = cycleId;
        this.LastCompletedUpperOrdinal = null;
        this.LastCompletedAtUtc = normalizedNow;
        this.CopyCompletedCounts(
            WorkspaceStaffIdentityAnchorSweepPageCounts.Empty);
        this.LastAdvanceId = advanceId;
        this.LastAdvanceSha256 = digest;
        this.LastRunId = runId;
        this.UpdatedAtUtc = normalizedNow;
        this.Version++;
        return Result.Success();
    }

    public Result Advance(
        long expectedVersion,
        Guid expectedCycleId,
        long? expectedAfterOrdinal,
        long nextAfterOrdinal,
        bool reachedEnd,
        Guid advanceId,
        Guid runId,
        WorkspaceStaffIdentityAnchorSweepPageCounts counts,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(counts);
        string digest = CreateAdvanceDigest(
            expectedVersion,
            expectedCycleId,
            expectedAfterOrdinal,
            nextAfterOrdinal,
            reachedEnd,
            runId,
            counts);
        if (this.LastAdvanceId == advanceId)
        {
            return expectedVersion == this.Version - 1 &&
                this.LastAdvanceSha256 == digest
                    ? Result.Success()
                    : Result.Failure(
                        WorkspaceStaffIdentityAnchorSweepErrors
                            .CheckpointConflict);
        }

        DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
        if (expectedVersion != this.Version ||
            expectedCycleId == Guid.Empty ||
            advanceId == Guid.Empty ||
            runId == Guid.Empty ||
            nextAfterOrdinal <= 0 ||
            this.CycleId != expectedCycleId ||
            !this.CycleUpperOrdinal.HasValue ||
            this.AfterOrdinal != expectedAfterOrdinal ||
            (expectedAfterOrdinal.HasValue &&
                expectedAfterOrdinal.Value <= 0) ||
            nextAfterOrdinal > this.CycleUpperOrdinal.Value ||
            (!reachedEnd &&
                nextAfterOrdinal >= this.CycleUpperOrdinal.Value) ||
            (expectedAfterOrdinal.HasValue &&
                nextAfterOrdinal <= expectedAfterOrdinal.Value) ||
            !counts.IsValid() ||
            (!reachedEnd && counts.ScannedCount == 0) ||
            normalizedNow < this.UpdatedAtUtc)
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorSweepErrors.CheckpointConflict);
        }

        if (!this.TryAccumulate(counts))
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorSweepErrors.CheckpointConflict);
        }

        this.AfterOrdinal = nextAfterOrdinal;
        this.LastAdvanceId = advanceId;
        this.LastAdvanceSha256 = digest;
        this.LastRunId = runId;
        this.UpdatedAtUtc = normalizedNow;
        if (reachedEnd)
        {
            this.CompleteActiveCycle(normalizedNow);
        }

        this.Version++;
        return Result.Success();
    }

    public WorkspaceStaffIdentityAnchorSweepPageCounts CurrentCounts() =>
        new(
            this.CycleScannedCount,
            this.CycleNoAnchorCount,
            this.CycleRemovedCount,
            this.CycleObservedCount,
            this.CycleAlreadyObservedCount,
            this.CycleDeferredCount,
            this.CycleConflictCount,
            this.CyclePassOneCommittedCount,
            this.CycleResolutionRecordConfirmedCount);

    public WorkspaceStaffIdentityAnchorSweepPageCounts LastCompletedCounts() =>
        new(
            this.LastCompletedScannedCount,
            this.LastCompletedNoAnchorCount,
            this.LastCompletedRemovedCount,
            this.LastCompletedObservedCount,
            this.LastCompletedAlreadyObservedCount,
            this.LastCompletedDeferredCount,
            this.LastCompletedConflictCount,
            this.LastCompletedPassOneCommittedCount,
            this.LastCompletedResolutionRecordConfirmedCount);

    private static string CreateEmptyAdvanceDigest(
        Guid cycleId,
        Guid runId) =>
        Sha256($"empty|{cycleId:D}|{runId:D}");

    private static string CreateAdvanceDigest(
        long expectedVersion,
        Guid cycleId,
        long? expectedAfterOrdinal,
        long nextAfterOrdinal,
        bool reachedEnd,
        Guid runId,
        WorkspaceStaffIdentityAnchorSweepPageCounts counts)
    {
        string after = expectedAfterOrdinal?.ToString(
            CultureInfo.InvariantCulture) ?? "null";
        string canonical = string.Join(
            '|',
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            cycleId.ToString("D"),
            after,
            nextAfterOrdinal.ToString(CultureInfo.InvariantCulture),
            reachedEnd ? "1" : "0",
            runId.ToString("D"),
            counts.ScannedCount.ToString(CultureInfo.InvariantCulture),
            counts.NoAnchorCount.ToString(CultureInfo.InvariantCulture),
            counts.RemovedCount.ToString(CultureInfo.InvariantCulture),
            counts.ObservedCount.ToString(CultureInfo.InvariantCulture),
            counts.AlreadyObservedCount.ToString(CultureInfo.InvariantCulture),
            counts.DeferredCount.ToString(CultureInfo.InvariantCulture),
            counts.ConflictCount.ToString(CultureInfo.InvariantCulture),
            counts.PassOneCommittedCount.ToString(CultureInfo.InvariantCulture),
            counts.ResolutionRecordConfirmedCount.ToString(
                CultureInfo.InvariantCulture));
        return Sha256(canonical);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private bool TryAccumulate(
        WorkspaceStaffIdentityAnchorSweepPageCounts counts)
    {
        try
        {
            long scanned = checked(
                this.CycleScannedCount + counts.ScannedCount);
            long noAnchor = checked(
                this.CycleNoAnchorCount + counts.NoAnchorCount);
            long removed = checked(
                this.CycleRemovedCount + counts.RemovedCount);
            long observed = checked(
                this.CycleObservedCount + counts.ObservedCount);
            long alreadyObserved = checked(
                this.CycleAlreadyObservedCount +
                counts.AlreadyObservedCount);
            long deferred = checked(
                this.CycleDeferredCount + counts.DeferredCount);
            long conflict = checked(
                this.CycleConflictCount + counts.ConflictCount);
            long passOneCommitted = checked(
                this.CyclePassOneCommittedCount +
                counts.PassOneCommittedCount);
            long resolutionRecordConfirmed = checked(
                this.CycleResolutionRecordConfirmedCount +
                counts.ResolutionRecordConfirmedCount);
            this.CycleScannedCount = scanned;
            this.CycleNoAnchorCount = noAnchor;
            this.CycleRemovedCount = removed;
            this.CycleObservedCount = observed;
            this.CycleAlreadyObservedCount = alreadyObserved;
            this.CycleDeferredCount = deferred;
            this.CycleConflictCount = conflict;
            this.CyclePassOneCommittedCount = passOneCommitted;
            this.CycleResolutionRecordConfirmedCount =
                resolutionRecordConfirmed;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void CompleteActiveCycle(DateTimeOffset nowUtc)
    {
        this.LastCompletedCycleId = this.CycleId;
        this.LastCompletedUpperOrdinal = this.CycleUpperOrdinal;
        this.LastCompletedAtUtc = nowUtc;
        this.CopyCompletedCounts(this.CurrentCounts());
        this.CycleId = null;
        this.CycleUpperOrdinal = null;
        this.AfterOrdinal = null;
        this.CycleStartedAtUtc = null;
        this.ResetCycleCounts();
    }

    private void CopyCompletedCounts(
        WorkspaceStaffIdentityAnchorSweepPageCounts counts)
    {
        this.LastCompletedScannedCount = counts.ScannedCount;
        this.LastCompletedNoAnchorCount = counts.NoAnchorCount;
        this.LastCompletedRemovedCount = counts.RemovedCount;
        this.LastCompletedObservedCount = counts.ObservedCount;
        this.LastCompletedAlreadyObservedCount =
            counts.AlreadyObservedCount;
        this.LastCompletedDeferredCount = counts.DeferredCount;
        this.LastCompletedConflictCount = counts.ConflictCount;
        this.LastCompletedPassOneCommittedCount =
            counts.PassOneCommittedCount;
        this.LastCompletedResolutionRecordConfirmedCount =
            counts.ResolutionRecordConfirmedCount;
    }

    private void ResetCycleCounts()
    {
        this.CycleScannedCount = 0;
        this.CycleNoAnchorCount = 0;
        this.CycleRemovedCount = 0;
        this.CycleObservedCount = 0;
        this.CycleAlreadyObservedCount = 0;
        this.CycleDeferredCount = 0;
        this.CycleConflictCount = 0;
        this.CyclePassOneCommittedCount = 0;
        this.CycleResolutionRecordConfirmedCount = 0;
    }
}
