namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class ExecuteTenantTerminationExportOwnerWorkTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    ITenantTerminationReplayStore replayStore,
    ITenantTerminationExportFragmentGenerator generator,
    ITenantTerminationTaskScheduler scheduler,
    ISystemClock clock,
    IScopeContext scopeContext)
    : ITaskHandler<ExecuteTenantTerminationExportOwnerWorkPayload>
{
    public async Task HandleAsync(
        ExecuteTenantTerminationExportOwnerWorkPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(payload, context, scopeContext);
        Result<TenantTerminationOwnerWorkStart> started =
            await commandDispatcher.DispatchAsync<
                BeginTenantTerminationOwnerWorkCommand,
                TenantTerminationOwnerWorkStart>(
                    context,
                    new(
                        payload.ProcessId,
                        payload.WorkItemId,
                        payload.OperationRevision,
                        TenantTerminationContributionPhase.Export,
                        payload.OwnerKey,
                        TenantTerminationExecutionBoundary.TenantScopedTask,
                        context.RunId,
                        context.Attempt),
                    cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (!started.Value.DispatchRequired)
        {
            await this.EnqueueReadyAsync(
                context.ScopeId!,
                started.Value.ReadyDispatches,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        TenantTerminationContributionRequest request =
            started.Value.Request ?? throw InvalidExecution();
        ValidateStart(started.Value, request, context.ScopeId!);
        TenantTerminationReplayAttempt replay = await this.PrepareReplayAsync(
            started.Value,
            request,
            context,
            cancellationToken).ConfigureAwait(false);

        Result<TenantTerminationExportFragmentGenerationStart> begun =
            await commandDispatcher.DispatchAsync<
                BeginTenantTerminationExportFragmentGenerationCommand,
                TenantTerminationExportFragmentGenerationStart>(
                    context,
                    new(
                        payload.ProcessId,
                        payload.WorkItemId,
                        payload.OperationRevision,
                        payload.OwnerKey,
                        context.RunId,
                        context.Attempt,
                        started.Value.WorkItemVersion),
                    cancellationToken).ConfigureAwait(false);
        if (begun.IsFailure)
        {
            throw Failure(begun.Error);
        }

        try
        {
            DataRightsDeadlineExecution<
                TenantTerminationProtectedExportFragment> execution =
                await this.GenerateWithDeadlineAsync(
                    begun.Value.Request,
                    cancellationToken).ConfigureAwait(false);
            TenantTerminationProtectedExportFragment protectedFragment =
                execution.Value;
            TenantTerminationContributionResult contribution =
                ToContribution(
                    protectedFragment.AssemblyResult.Owner,
                    started.Value);
            if (replay.Result is null)
            {
                TenantTerminationReplayResult result =
                    TenantTerminationReplayResult.Create(
                        replay.Dispatch,
                        contribution,
                        execution.ObservedAtUtc);
                TenantTerminationReplayJournalEntry entry =
                    TenantTerminationReplayJournalEntry.ForResult(
                        replay.Dispatch,
                        result);
                TenantTerminationReplayAppendReceipt receipt =
                    await replayStore.AppendAsync(entry, cancellationToken)
                        .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                TenantTerminationReplayDurability.EnsureMatches(
                    receipt,
                    entry);
            }

            Result<TenantTerminationOwnerResultRecorded> completed =
                await commandDispatcher.DispatchAsync<
                    CompleteTenantTerminationExportFragmentGenerationCommand,
                    TenantTerminationOwnerResultRecorded>(
                        context,
                        new(
                            payload.ProcessId,
                            payload.WorkItemId,
                            payload.OperationRevision,
                            payload.OwnerKey,
                            context.RunId,
                            context.Attempt,
                            started.Value.WorkItemVersion,
                            begun.Value.FragmentVersion,
                            protectedFragment),
                        cancellationToken).ConfigureAwait(false);
            if (completed.IsFailure)
            {
                throw Failure(completed.Error);
            }

            await this.EnqueueReadyAsync(
                request.TenantId,
                completed.Value.ReadyDispatches,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await this.TryRecordFailureAsync(
                payload,
                begun.Value.FragmentVersion,
                FailureCode(exception),
                context,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TenantTerminationReplayAttempt> PrepareReplayAsync(
        TenantTerminationOwnerWorkStart started,
        TenantTerminationContributionRequest request,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
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
        if (replay is not null)
        {
            ValidateReplay(started, request, context, replay);
            return replay;
        }

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
        return new TenantTerminationReplayAttempt(dispatch, Result: null);
    }

    private async Task<DataRightsDeadlineExecution<
        TenantTerminationProtectedExportFragment>>
        GenerateWithDeadlineAsync(
            TenantTerminationExportFragmentGenerationRequest request,
            CancellationToken cancellationToken)
    {
        return await DataRightsDeadlineExecutor.ExecuteAsync(
            clock,
            request.AssemblyRequest.DeadlineUtc,
            "DataRights.TenantTerminationOwnerDeadlineExceeded",
            token => generator.GenerateAsync(request, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRecordFailureAsync(
        ExecuteTenantTerminationExportOwnerWorkPayload payload,
        long expectedFragmentVersion,
        string failureCode,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await commandDispatcher.DispatchAsync<
                FailTenantTerminationExportFragmentGenerationCommand,
                Gma.Framework.Cqrs.Unit>(
                    context,
                    new FailTenantTerminationExportFragmentGenerationCommand(
                        payload.ProcessId,
                        payload.WorkItemId,
                        payload.OperationRevision,
                        context.RunId,
                        context.Attempt,
                        expectedFragmentVersion,
                        failureCode),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original task failure remains authoritative for Task Runtime.
        }
    }

    private Task EnqueueReadyAsync(
        string tenantId,
        IReadOnlyCollection<TenantTerminationPlannedDispatch> ready,
        CancellationToken cancellationToken) =>
        ready.Count == 0
            ? Task.CompletedTask
            : scheduler.EnqueueAsync(tenantId, ready, cancellationToken);

    private static void ValidateBoundary(
        ExecuteTenantTerminationExportOwnerWorkPayload payload,
        TaskExecutionContext context,
        IScopeContext scopeContext)
    {
        if (payload.ProcessId == Guid.Empty ||
            payload.WorkItemId == Guid.Empty ||
            payload.OperationRevision <= 0 ||
            string.IsNullOrWhiteSpace(payload.OwnerKey) ||
            !scopeContext.IsEnabled ||
            !string.Equals(
                scopeContext.ScopeId,
                context.ScopeId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(context.ScopeId) ||
            !string.Equals(
                context.ModuleName,
                DataRightsModuleMetadata.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.TaskName,
                ExecuteTenantTerminationExportOwnerWorkPayload.TaskName,
                StringComparison.Ordinal) ||
            context.PayloadVersion !=
                ExecuteTenantTerminationExportOwnerWorkPayload.PayloadVersion ||
            context.CorrelationId != payload.ProcessId)
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }
    }

    private static void ValidateStart(
        TenantTerminationOwnerWorkStart started,
        TenantTerminationContributionRequest request,
        string tenantId)
    {
        if (started.ExecutionBoundary !=
                TenantTerminationExecutionBoundary.TenantScopedTask ||
            request.Phase != TenantTerminationContributionPhase.Export ||
            !string.Equals(request.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }
    }

    private static void ValidateReplay(
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
    }

    private static TenantTerminationContributionResult ToContribution(
        TenantTerminationExportOwnerResult owner,
        TenantTerminationOwnerWorkStart started) => new(
            TenantTerminationContributionStatus.Completed,
            owner.ResultCode,
            owner.RecordCount,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            owner.SelectedProofRevision,
            owner.ResultingProofRevision,
            started.CatalogVersion,
            started.CatalogSha256,
            owner.RecordedAtUtc);

    private static string FailureCode(Exception exception) =>
        exception switch
        {
            DataRightsExportGenerationException generation => generation.Code,
            TimeoutException => "owner-export-deadline-exceeded",
            _ => "owner-export-generation-failed"
        };

    private static InvalidOperationException InvalidExecution() =>
        new("DataRights.TenantTerminationRequestUnavailable");

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
