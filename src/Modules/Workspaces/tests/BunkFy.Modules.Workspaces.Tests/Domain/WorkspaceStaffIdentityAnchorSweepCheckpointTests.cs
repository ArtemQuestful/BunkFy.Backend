namespace BunkFy.Modules.Workspaces.Tests.Domain;

using BunkFy.Modules.Workspaces.Domain;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorSweepCheckpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Advance_is_exactly_idempotent_and_cycles_without_a_lifetime_cap()
    {
        const long upper = 30;
        const long firstCursor = 20;
        Guid cycleId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        WorkspaceStaffIdentityAnchorSweepCheckpoint checkpoint =
            WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                Now).Value;
        Assert.True(checkpoint.BeginCycle(
            cycleId,
            upper,
            runId,
            Now.AddMinutes(1)).IsSuccess);
        long expectedVersion = checkpoint.Version;
        WorkspaceStaffIdentityAnchorSweepPageCounts firstCounts = new(
            ScannedCount: 2,
            NoAnchorCount: 1,
            RemovedCount: 0,
            ObservedCount: 1,
            AlreadyObservedCount: 0,
            DeferredCount: 0,
            ConflictCount: 0,
            PassOneCommittedCount: 1,
            ResolutionRecordConfirmedCount: 0);
        Guid firstAdvanceId = Guid.NewGuid();

        Assert.True(checkpoint.Advance(
            expectedVersion,
            cycleId,
            expectedAfterOrdinal: null,
            firstCursor,
            reachedEnd: false,
            firstAdvanceId,
            runId,
            firstCounts,
            Now.AddMinutes(2)).IsSuccess);
        long advancedVersion = checkpoint.Version;

        Assert.True(checkpoint.Advance(
            expectedVersion,
            cycleId,
            expectedAfterOrdinal: null,
            firstCursor,
            reachedEnd: false,
            firstAdvanceId,
            runId,
            firstCounts,
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(advancedVersion, checkpoint.Version);
        WorkspaceStaffIdentityAnchorSweepPageCounts changedReplay =
            firstCounts with { ConflictCount = 1, ObservedCount = 0 };
        Assert.True(checkpoint.Advance(
            expectedVersion,
            cycleId,
            expectedAfterOrdinal: null,
            firstCursor,
            reachedEnd: false,
            firstAdvanceId,
            runId,
            changedReplay,
            Now.AddMinutes(3)).IsFailure);
        Assert.True(checkpoint.Advance(
            expectedVersion,
            cycleId,
            expectedAfterOrdinal: null,
            firstCursor,
            reachedEnd: false,
            Guid.NewGuid(),
            runId,
            firstCounts,
            Now.AddMinutes(3)).IsFailure);

        WorkspaceStaffIdentityAnchorSweepPageCounts finalCounts = new(
            ScannedCount: 1,
            NoAnchorCount: 0,
            RemovedCount: 0,
            ObservedCount: 0,
            AlreadyObservedCount: 1,
            DeferredCount: 0,
            ConflictCount: 0,
            PassOneCommittedCount: 0,
            ResolutionRecordConfirmedCount: 1);
        Assert.True(checkpoint.Advance(
            checkpoint.Version,
            cycleId,
            firstCursor,
            upper,
            reachedEnd: true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            finalCounts,
            Now.AddMinutes(4)).IsSuccess);

        Assert.False(checkpoint.HasActiveCycle);
        Assert.Null(checkpoint.AfterOrdinal);
        Assert.Equal(cycleId, checkpoint.LastCompletedCycleId);
        Assert.Equal(upper, checkpoint.LastCompletedUpperOrdinal);
        Assert.Equal(3, checkpoint.LastCompletedScannedCount);
        Assert.Equal(1, checkpoint.LastCompletedObservedCount);
        Assert.Equal(1, checkpoint.LastCompletedAlreadyObservedCount);
        Assert.Equal(2, checkpoint.LastCompletedCounts().SettledCount);

        Guid nextCycleId = Guid.NewGuid();
        Assert.True(checkpoint.BeginCycle(
            nextCycleId,
            upperOrdinal: 40,
            Guid.NewGuid(),
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(checkpoint.HasActiveCycle);
        Assert.Equal(nextCycleId, checkpoint.CycleId);
        Assert.Null(checkpoint.AfterOrdinal);
        Assert.Equal(0, checkpoint.CycleScannedCount);
    }

    [Fact]
    public void Empty_cycle_replay_is_bound_to_the_same_cycle_and_run()
    {
        WorkspaceStaffIdentityAnchorSweepCheckpoint checkpoint =
            WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                Now).Value;
        Guid cycleId = Guid.NewGuid();
        Guid advanceId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();

        Assert.True(checkpoint.CompleteEmptyCycle(
            cycleId,
            advanceId,
            runId,
            Now.AddMinutes(1)).IsSuccess);
        long completedVersion = checkpoint.Version;
        Assert.True(checkpoint.CompleteEmptyCycle(
            cycleId,
            advanceId,
            runId,
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(completedVersion, checkpoint.Version);
        Assert.True(checkpoint.CompleteEmptyCycle(
            Guid.NewGuid(),
            advanceId,
            runId,
            Now.AddMinutes(2)).IsFailure);
        Assert.NotNull(checkpoint.LastCompletedAtUtc);
        Assert.Equal(0, checkpoint.LastCompletedCounts().BacklogCount);

        Assert.True(checkpoint.BeginCycle(
            Guid.NewGuid(),
            upperOrdinal: 10,
            Guid.NewGuid(),
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(checkpoint.CompleteEmptyCycle(
            cycleId,
            advanceId,
            runId,
            Now.AddMinutes(4)).IsFailure);
    }
}
