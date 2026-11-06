namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using System.Runtime.CompilerServices;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.Termination;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffIdentityAnchorSweepRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffIdentityAnchorSweepRepository
{
    public async Task<Result<WorkspaceStaffIdentityAnchorSweepPage>>
        PreparePageAsync(
            string scopeId,
            Guid checkpointId,
            Guid cycleId,
            Guid emptyAdvanceId,
            Guid runId,
            int batchSize,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scopeId) ||
            checkpointId == Guid.Empty ||
            cycleId == Guid.Empty ||
            emptyAdvanceId == Guid.Empty ||
            runId == Guid.Empty ||
            batchSize is <= 0 or > 500 ||
            nowUtc == default)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorSweepPage>(
                WorkspaceStaffIdentityAnchorSweepErrors.Invalid);
        }

        DbSet<WorkspaceStaffIdentityAnchorSweepCheckpoint> checkpoints =
            dbContext.Set<WorkspaceStaffIdentityAnchorSweepCheckpoint>();
        WorkspaceStaffIdentityAnchorSweepCheckpoint? checkpoint =
            await checkpoints.SingleOrDefaultAsync(
                    candidate => candidate.ScopeId == scopeId &&
                        candidate.ProtocolVersion ==
                        WorkspaceStaffIdentityAnchorSweepCheckpoint
                            .CurrentProtocolVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        if (checkpoint is null)
        {
            Result<WorkspaceStaffIdentityAnchorSweepCheckpoint> created =
                WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                    checkpointId,
                    scopeId,
                    nowUtc);
            if (created.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffIdentityAnchorSweepPage>(created.Error);
            }

            checkpoint = created.Value;
            checkpoints.Add(checkpoint);
        }

        if (!checkpoint.HasActiveCycle)
        {
            long? upper = await dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .Where(application => application.ScopeId == scopeId)
                .OrderByDescending(application =>
                    application.IdentityAnchorSweepOrdinal)
                .Select(application =>
                    (long?)application.IdentityAnchorSweepOrdinal)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!upper.HasValue)
            {
                Result completed = checkpoint.CompleteEmptyCycle(
                    cycleId,
                    emptyAdvanceId,
                    runId,
                    nowUtc);
                return completed.IsSuccess
                    ? Result.Success(
                        new WorkspaceStaffIdentityAnchorSweepPage(
                            checkpoint.Id,
                            checkpoint.Version,
                            cycleId,
                            null,
                            null,
                            null,
                            ReachedEnd: true,
                            AdvanceRequired: false,
                            []))
                    : Result.Failure<
                        WorkspaceStaffIdentityAnchorSweepPage>(
                            completed.Error);
            }

            Result begun = checkpoint.BeginCycle(
                cycleId,
                upper.Value,
                runId,
                nowUtc);
            if (begun.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffIdentityAnchorSweepPage>(begun.Error);
            }
        }

        Guid expectedCycleId = checkpoint.CycleId!.Value;
        long upperOrdinal = checkpoint.CycleUpperOrdinal!.Value;
        long? expectedAfterOrdinal = checkpoint.AfterOrdinal;
        IQueryable<WorkspaceStaffOnboarding> query = dbContext
            .StaffOnboardingApplications
            .AsNoTracking()
            .ExcludeExactlyReviewed(dbContext)
            .Where(application =>
                application.ScopeId == scopeId &&
                application.IdentityAnchorSweepOrdinal <= upperOrdinal);
        if (expectedAfterOrdinal.HasValue)
        {
            long after = expectedAfterOrdinal.Value;
            query = query.Where(application =>
                application.IdentityAnchorSweepOrdinal > after);
        }

        WorkspaceStaffIdentityAnchorSweepCandidate[] loaded = await query
            .OrderBy(application => application.IdentityAnchorSweepOrdinal)
            .Select(application =>
                new WorkspaceStaffIdentityAnchorSweepCandidate(
                    application.Id,
                    application.IdentityAnchorSweepOrdinal,
                    application.SubjectId,
                    application.StaffMemberId != null ||
                    application.IdentityAnchorExpectedResolutionEventId !=
                        null ||
                    application.IdentityAnchorContinuationEventId != null ||
                    application.IdentityAnchorResolutionEventId != null ||
                    application.IdentityAnchorResolutionStaffMemberId != null ||
                    application.IdentityAnchorResolutionApplicationVersion !=
                        null ||
                    application.IdentityAnchorResolutionDisposition != null ||
                    application.IdentityAnchorResolutionIntentAtUtc != null ||
                    application.IdentityAnchorResolutionObservedAtUtc != null))
            .Take(batchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool reachedEnd = loaded.Length <= batchSize;
        WorkspaceStaffIdentityAnchorSweepCandidate[] selected = loaded
            .Take(batchSize)
            .ToArray();
        long nextAfterOrdinal = selected.Length == 0
            ? upperOrdinal
            : selected[^1].IdentityAnchorSweepOrdinal;
        return Result.Success(
            new WorkspaceStaffIdentityAnchorSweepPage(
                checkpoint.Id,
                checkpoint.Version,
                expectedCycleId,
                upperOrdinal,
                expectedAfterOrdinal,
                nextAfterOrdinal,
                reachedEnd,
                AdvanceRequired: true,
                Array.AsReadOnly(selected)));
    }

    public async Task<Result> AdvanceAsync(
        WorkspaceStaffIdentityAnchorSweepAdvance advance,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advance);
        WorkspaceStaffIdentityAnchorSweepCheckpoint? checkpoint =
            await dbContext
                .Set<WorkspaceStaffIdentityAnchorSweepCheckpoint>()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == advance.CheckpointId,
                    cancellationToken)
                .ConfigureAwait(false);
        return checkpoint is null
            ? Result.Failure(
                WorkspaceStaffIdentityAnchorSweepErrors.CheckpointConflict)
            : checkpoint.Advance(
                advance.ExpectedCheckpointVersion,
                advance.ExpectedCycleId,
                advance.ExpectedAfterOrdinal,
                advance.NextAfterOrdinal,
                advance.ReachedEnd,
                advance.AdvanceId,
                advance.RunId,
                advance.Counts,
                nowUtc);
    }

    public async Task<WorkspaceStaffIdentityAnchorSweepStatus> GetStatusAsync(
        string scopeId,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffIdentityAnchorSweepCheckpoint? checkpoint =
            await dbContext
                .Set<WorkspaceStaffIdentityAnchorSweepCheckpoint>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.ScopeId == scopeId &&
                        candidate.ProtocolVersion ==
                        WorkspaceStaffIdentityAnchorSweepCheckpoint
                            .CurrentProtocolVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        return checkpoint is null
            ? new WorkspaceStaffIdentityAnchorSweepStatus(
                scopeId,
                HasCheckpoint: false,
                WorkspaceStaffIdentityAnchorSweepCheckpoint
                    .CurrentProtocolVersion,
                CheckpointVersion: 0,
                HasActiveCycle: false,
                CycleId: null,
                CycleUpperOrdinal: null,
                AfterOrdinal: null,
                CycleStartedAtUtc: null,
                WorkspaceStaffIdentityAnchorSweepPageCounts.Empty,
                LastCompletedCycleId: null,
                LastCompletedUpperOrdinal: null,
                LastCompletedAtUtc: null,
                WorkspaceStaffIdentityAnchorSweepPageCounts.Empty,
                LastRunId: null,
                UpdatedAtUtc: null,
                HasCompletedBoundedCycle: false)
            : new WorkspaceStaffIdentityAnchorSweepStatus(
                checkpoint.ScopeId,
                HasCheckpoint: true,
                checkpoint.ProtocolVersion,
                checkpoint.Version,
                checkpoint.HasActiveCycle,
                checkpoint.CycleId,
                checkpoint.CycleUpperOrdinal,
                checkpoint.AfterOrdinal,
                checkpoint.CycleStartedAtUtc,
                checkpoint.CurrentCounts(),
                checkpoint.LastCompletedCycleId,
                checkpoint.LastCompletedUpperOrdinal,
                checkpoint.LastCompletedAtUtc,
                checkpoint.LastCompletedCounts(),
                checkpoint.LastRunId,
                checkpoint.UpdatedAtUtc,
                HasCompletedBoundedCycle:
                    checkpoint.LastCompletedCycleId.HasValue &&
                    checkpoint.LastCompletedAtUtc.HasValue);
    }

    public async IAsyncEnumerable<string> StreamScheduleScopeIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IQueryable<WorkspaceTerminationFence> fences = dbContext
            .WorkspaceTerminationFences
            .IgnoreQueryFilters()
            .AsNoTracking();
        IQueryable<string> scopes = dbContext.StaffOnboardingApplications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(application => !fences.Any(fence =>
                fence.ScopeId == application.ScopeId &&
                fence.State != WorkspaceTerminationFenceState.Released))
            .Select(application => application.ScopeId)
            .Distinct()
            .Order();
        await foreach (string scopeId in scopes
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return scopeId;
        }
    }
}
