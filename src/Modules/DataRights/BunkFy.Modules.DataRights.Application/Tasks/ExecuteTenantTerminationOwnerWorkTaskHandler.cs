namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class ExecuteTenantTerminationOwnerWorkTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    ITenantTerminationReplayStore replayStore,
    IEnumerable<ITenantTerminationContributor> contributors,
    ISystemClock clock,
    IScopeContext scopeContext,
    ITenantTerminationTaskScheduler scheduler)
    : ITaskHandler<ExecuteTenantTerminationOwnerWorkPayload>
{
    public Task HandleAsync(
        ExecuteTenantTerminationOwnerWorkPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken) =>
        this.ExecuteAsync(
            payload.ProcessId,
            payload.WorkItemId,
            payload.OperationRevision,
            payload.Phase,
            payload.OwnerKey,
            context.ScopeId,
            TenantTerminationExecutionBoundary.TenantScopedTask,
            ExecuteTenantTerminationOwnerWorkPayload.TaskName,
            ExecuteTenantTerminationOwnerWorkPayload.PayloadVersion,
            context,
            cancellationToken);

    internal Task HandleGlobalAsync(
        ExecuteGlobalTenantTerminationOwnerWorkPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken) =>
        this.ExecuteAsync(
            payload.ProcessId,
            payload.WorkItemId,
            payload.OperationRevision,
            payload.Phase,
            payload.OwnerKey,
            payload.TenantId,
            TenantTerminationExecutionBoundary.GlobalControlTask,
            ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName,
            ExecuteGlobalTenantTerminationOwnerWorkPayload.PayloadVersion,
            context,
            cancellationToken);

    private async Task ExecuteAsync(
        Guid processId,
        Guid workItemId,
        long operationRevision,
        TenantTerminationContributionPhase phase,
        string ownerKey,
        string? expectedTenantId,
        TenantTerminationExecutionBoundary expectedBoundary,
        string expectedTaskName,
        int expectedPayloadVersion,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (processId == Guid.Empty ||
            workItemId == Guid.Empty ||
            operationRevision <= 0 ||
            phase == TenantTerminationContributionPhase.Unknown ||
            string.IsNullOrWhiteSpace(ownerKey) ||
            string.IsNullOrWhiteSpace(expectedTenantId) ||
            !scopeContext.IsEnabled ||
            !string.Equals(
                scopeContext.ScopeId,
                expectedTenantId,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.ModuleName,
                DataRightsModuleMetadata.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.TaskName,
                expectedTaskName,
                StringComparison.Ordinal) ||
            context.PayloadVersion != expectedPayloadVersion ||
            context.CorrelationId != processId ||
            (expectedBoundary ==
                TenantTerminationExecutionBoundary.TenantScopedTask
                    ? !string.Equals(
                        context.ScopeId,
                        expectedTenantId,
                        StringComparison.Ordinal)
                    : context.ScopeId is not null))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }

        Result<TenantTerminationOwnerWorkStart> started =
            await commandDispatcher.DispatchAsync<
                BeginTenantTerminationOwnerWorkCommand,
                TenantTerminationOwnerWorkStart>(
                    context,
                    new(
                        processId,
                        workItemId,
                        operationRevision,
                        phase,
                        ownerKey,
                        expectedBoundary,
                        context.RunId,
                        context.Attempt),
                    cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (!started.Value.DispatchRequired)
        {
            if (started.Value.ReadyDispatches.Count > 0)
            {
                await scheduler.EnqueueAsync(
                    expectedTenantId,
                    started.Value.ReadyDispatches,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        TenantTerminationContributionRequest request =
            started.Value.Request ?? throw new InvalidOperationException(
                "DataRights.TenantTerminationRequestUnavailable");
        if (started.Value.ExecutionBoundary != expectedBoundary ||
            !string.Equals(
                expectedTenantId,
                request.TenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }

        if (request.Phase == TenantTerminationContributionPhase.Export)
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExportExecutorRequired");
        }

        ITenantTerminationContributor contributor = ResolveContributor(
            started.Value,
            contributors);
        TenantTerminationReplayAttemptCoordinate coordinate = new(
            request.TenantId,
            request.ProcessId,
            request.WorkItemId,
            context.RunId,
            context.Attempt);
        TenantTerminationReplayAttempt? replay =
            await replayStore.ReadAttemptAsync(
                coordinate,
                cancellationToken).ConfigureAwait(false);
        TenantTerminationReplayDispatch dispatch = replay is null
            ? await this.ProtectDispatchAsync(
                started.Value,
                request,
                context,
                cancellationToken).ConfigureAwait(false)
            : ValidateReplay(started.Value, request, context, replay);

        if (replay?.Result is null)
        {
            DataRightsDeadlineExecution<TenantTerminationContributionResult>
                execution =
                await this.ExecuteWithDeadlineAsync(
                    contributor,
                    request,
                    cancellationToken).ConfigureAwait(false);
            TenantTerminationContributionResult contribution = execution.Value;
            TenantTerminationReplayResult result;
            try
            {
                result = TenantTerminationReplayResult.Create(
                    dispatch,
                    contribution,
                    execution.ObservedAtUtc);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    "DataRights.TenantTerminationOwnerResultInvalid",
                    exception);
            }

            TenantTerminationReplayJournalEntry entry =
                TenantTerminationReplayJournalEntry.ForResult(
                    dispatch,
                    result);
            TenantTerminationReplayAppendReceipt receipt =
                await replayStore.AppendAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TenantTerminationReplayDurability.EnsureMatches(receipt, entry);
        }

        Result<TenantTerminationOwnerResultRecorded> recorded =
            await commandDispatcher.DispatchAsync<
                RecordTenantTerminationOwnerResultCommand,
                TenantTerminationOwnerResultRecorded>(
                    context,
                    new(
                        processId,
                        workItemId,
                        operationRevision,
                        phase,
                        ownerKey,
                        context.RunId,
                        context.Attempt,
                        started.Value.WorkItemVersion),
                    cancellationToken).ConfigureAwait(false);
        if (recorded.IsFailure)
        {
            throw Failure(recorded.Error);
        }

        if (recorded.Value.ReadyDispatches.Count > 0)
        {
            await scheduler.EnqueueAsync(
                request.TenantId,
                recorded.Value.ReadyDispatches,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TenantTerminationReplayDispatch> ProtectDispatchAsync(
        TenantTerminationOwnerWorkStart started,
        TenantTerminationContributionRequest request,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        DateTimeOffset recordedAtUtc = clock.UtcNow;
        if (recordedAtUtc >= request.DeadlineUtc)
        {
            throw new TimeoutException(
                "DataRights.TenantTerminationOwnerDeadlineExceeded");
        }

        TenantTerminationReplayDispatch dispatch =
            TenantTerminationReplayDispatch.Create(
                request,
                started.OwnerKey,
                started.CatalogVersion,
                started.CatalogSha256,
                started.ExecutionBoundary,
                context.RunId,
                context.Attempt,
                recordedAtUtc);
        TenantTerminationReplayJournalEntry entry =
            TenantTerminationReplayJournalEntry.ForDispatch(dispatch);
        TenantTerminationReplayAppendReceipt receipt =
            await replayStore.AppendAsync(entry, cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        TenantTerminationReplayDurability.EnsureMatches(receipt, entry);
        return dispatch;
    }

    private static TenantTerminationReplayDispatch ValidateReplay(
        TenantTerminationOwnerWorkStart started,
        TenantTerminationContributionRequest request,
        TaskExecutionContext context,
        TenantTerminationReplayAttempt replay)
    {
        DateTimeOffset startedAtUtc = request.DeadlineUtc.Subtract(
            BeginTenantTerminationOwnerWorkCommandHandler.OwnerCallTimeout);
        if (replay.Dispatch.RecordedAtUtc < startedAtUtc ||
            !TenantTerminationReplayProof.MatchesDispatch(
                replay.Dispatch,
                request,
                started.OwnerKey,
                started.CatalogVersion,
                started.CatalogSha256,
                started.ExecutionBoundary,
                context.RunId,
                context.Attempt) ||
            (replay.Result is not null &&
             !replay.Result.HasValidProof(replay.Dispatch)))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationReplayProofInvalid");
        }

        return replay.Dispatch;
    }

    private async Task<
        DataRightsDeadlineExecution<TenantTerminationContributionResult>>
        ExecuteWithDeadlineAsync(
            ITenantTerminationContributor contributor,
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken)
    {
        return await DataRightsDeadlineExecutor.ExecuteAsync(
            clock,
            request.DeadlineUtc,
            "DataRights.TenantTerminationOwnerDeadlineExceeded",
            token => contributor.ExecuteAsync(request, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static ITenantTerminationContributor ResolveContributor(
        TenantTerminationOwnerWorkStart started,
        IEnumerable<ITenantTerminationContributor> contributors)
    {
        ITenantTerminationContributor[] matches = contributors
            .Where(contributor =>
                string.Equals(
                    contributor.Descriptor.OwnerKey,
                    started.OwnerKey,
                    StringComparison.Ordinal) &&
                contributor.Descriptor.ContractVersion ==
                    started.Request!.ContractVersion &&
                contributor.Descriptor.CatalogVersion ==
                    started.CatalogVersion &&
                string.Equals(
                    contributor.Descriptor.CatalogSha256,
                    started.CatalogSha256,
                    StringComparison.Ordinal) &&
                contributor.Descriptor.PhasePlans.Any(plan =>
                    plan.Phase == started.Request.Phase &&
                    plan.ExecutionBoundary == started.ExecutionBoundary))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "DataRights.TenantTerminationOwnerContributorUnavailable");
    }

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
