namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class VerifyTenantTerminationTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    ITenantTerminationReplayStore replayStore,
    IEnumerable<ITenantTerminationContributor> contributors,
    ISystemClock clock,
    IScopeContext scopeContext)
    : ITaskHandler<VerifyTenantTerminationPayload>
{
    private readonly ITenantTerminationContributor[] contributors =
        contributors?.ToArray() ??
        throw new ArgumentNullException(nameof(contributors));

    public async Task HandleAsync(
        VerifyTenantTerminationPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(payload, context, scopeContext);
        Result<TenantTerminationVerificationStart> started =
            await commandDispatcher.DispatchAsync<
                PrepareTenantTerminationVerificationCommand,
                TenantTerminationVerificationStart>(
                    context,
                    new(
                        payload.ProcessId,
                        payload.OperationRevision,
                        context.RunId,
                        context.Attempt),
                    cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (!started.Value.DispatchRequired)
        {
            return;
        }

        List<DateTimeOffset> protectedAtUtc = [];
        foreach (TenantTerminationOwnerWorkItem workItem in
                 started.Value.WorkItems)
        {
            ITenantTerminationContributor contributor =
                this.ResolveContributor(workItem);
            TenantTerminationContributorPhasePlan phasePlan =
                contributor.Descriptor.PhasePlans.Single(plan =>
                    plan.Phase ==
                        TenantTerminationContributionPhase.Destroy);
            TenantTerminationReplayAttempt replay =
                await ReadAndValidateReplayAsync(
                    workItem,
                    contributor.Descriptor,
                    phasePlan.ExecutionBoundary,
                    replayStore,
                    cancellationToken).ConfigureAwait(false);
            TenantTerminationContributionResult liveResult =
                await ExecuteVerificationAsync(
                    contributor,
                    replay.Dispatch,
                    clock,
                    cancellationToken).ConfigureAwait(false);
            if (!Equals(liveResult, replay.Result!.Contribution) ||
                !TenantTerminationVerificationProofSet.MatchesWorkItem(
                    workItem,
                    liveResult))
            {
                throw InvalidProof();
            }

            protectedAtUtc.Add(replay.Result.ProtectedAtUtc);
        }

        TenantTerminationReplayCheckpoint checkpoint =
            await replayStore.ReadTrustedCheckpointAsync(
                context.ScopeId!,
                payload.ProcessId,
                cancellationToken).ConfigureAwait(false);
        if (protectedAtUtc.Count != started.Value.WorkItems.Count ||
            checkpoint.FlushedAtUtc < protectedAtUtc.Max())
        {
            throw InvalidProof();
        }

        Result<TenantTerminationVerificationCompleted> completed =
            await commandDispatcher.DispatchAsync<
                CompleteTenantTerminationVerificationCommand,
                TenantTerminationVerificationCompleted>(
                    context,
                    new(
                        payload.ProcessId,
                        started.Value.DestroyOperationRevision,
                        started.Value.VerificationOperationRevision,
                        context.RunId,
                        context.Attempt,
                        started.Value.ProcessVersion,
                        started.Value.OwnerProofSetSha256,
                        started.Value.TerminalOwnerKey,
                        checkpoint),
                    cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            throw Failure(completed.Error);
        }
    }

    private ITenantTerminationContributor ResolveContributor(
        TenantTerminationOwnerWorkItem workItem)
    {
        ITenantTerminationContributor[] matches = this.contributors
            .Where(contributor =>
                string.Equals(
                    contributor.Descriptor.OwnerKey,
                    workItem.OwnerKey,
                    StringComparison.Ordinal) &&
                contributor.Descriptor.ContractVersion ==
                    workItem.OwnerContractVersion &&
                contributor.Descriptor.CatalogVersion ==
                    workItem.CatalogVersion &&
                string.Equals(
                    contributor.Descriptor.CatalogSha256,
                    workItem.CatalogSha256,
                    StringComparison.Ordinal) &&
                contributor.Descriptor.PhasePlans.Any(plan =>
                    plan.Phase ==
                        TenantTerminationContributionPhase.Destroy))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw InvalidProof();
    }

    private static async Task<TenantTerminationReplayAttempt>
        ReadAndValidateReplayAsync(
            TenantTerminationOwnerWorkItem workItem,
            TenantTerminationContributorDescriptor descriptor,
            TenantTerminationExecutionBoundary executionBoundary,
            ITenantTerminationReplayStore replayStore,
            CancellationToken cancellationToken)
    {
        if (workItem.TaskRunId is not Guid taskRunId ||
            workItem.LastTaskAttempt <= 0)
        {
            throw InvalidProof();
        }

        TenantTerminationReplayAttemptCoordinate coordinate = new(
            workItem.ScopeId,
            workItem.ProcessId,
            workItem.Id,
            taskRunId,
            workItem.LastTaskAttempt);
        TenantTerminationReplayAttempt? replay =
            await replayStore.ReadAttemptAsync(
                coordinate,
                cancellationToken).ConfigureAwait(false);
        if (replay?.Result is null ||
            !replay.Result.HasValidProof(replay.Dispatch))
        {
            throw InvalidProof();
        }

        TenantTerminationContributionRequest originalRequest = new(
            workItem.OwnerContractVersion,
            workItem.ScopeId,
            workItem.ProcessId,
            workItem.CaseId,
            workItem.ApprovalRevision,
            workItem.OperationRevision,
            workItem.TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            workItem.Id,
            workItem.IdempotencyKey,
            workItem.PolicyEvidenceSha256,
            replay.Dispatch.ExecutingActorId,
            replay.Dispatch.DeadlineUtc);
        if (!TenantTerminationReplayProof.MatchesDispatch(
                replay.Dispatch,
                originalRequest,
                descriptor.OwnerKey,
                descriptor.CatalogVersion,
                descriptor.CatalogSha256,
                executionBoundary,
                taskRunId,
                workItem.LastTaskAttempt) ||
            !TenantTerminationVerificationProofSet.MatchesWorkItem(
                workItem,
                replay.Result.Contribution))
        {
            throw InvalidProof();
        }

        return replay;
    }

    private static async Task<TenantTerminationContributionResult>
        ExecuteVerificationAsync(
            ITenantTerminationContributor contributor,
            TenantTerminationReplayDispatch dispatch,
            ISystemClock clock,
            CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = clock.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.Add(
            BeginTenantTerminationOwnerWorkCommandHandler.OwnerCallTimeout);
        TenantTerminationContributionRequest request = new(
            dispatch.OwnerContractVersion,
            dispatch.Coordinate.TenantId,
            dispatch.Coordinate.ProcessId,
            dispatch.CaseId,
            dispatch.ApprovalRevision,
            dispatch.OperationRevision,
            dispatch.TerminationEpoch,
            dispatch.Phase,
            dispatch.Coordinate.WorkItemId,
            dispatch.IdempotencyKey,
            dispatch.PolicyEvidenceSha256,
            dispatch.ExecutingActorId,
            deadlineUtc);
        DataRightsDeadlineExecution<TenantTerminationContributionResult>
            execution = await DataRightsDeadlineExecutor.ExecuteAsync(
            clock,
            deadlineUtc,
            "DataRights.TenantTerminationVerificationDeadlineExceeded",
            token => contributor.ExecuteAsync(request, token),
            cancellationToken).ConfigureAwait(false);
        return execution.Value;
    }

    private static void ValidateBoundary(
        VerifyTenantTerminationPayload payload,
        TaskExecutionContext context,
        IScopeContext scopeContext)
    {
        Guid expectedRunId = TenantTerminationExecutionIdentity
            .CreateVerificationTaskRunId(
                payload.ProcessId,
                payload.OperationRevision);
        if (payload.ProcessId == Guid.Empty ||
            payload.OperationRevision <= 0 ||
            context.RunId != expectedRunId ||
            !scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(context.ScopeId) ||
            !string.Equals(
                scopeContext.ScopeId,
                context.ScopeId,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.ModuleName,
                DataRightsModuleMetadata.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.TaskName,
                VerifyTenantTerminationPayload.TaskName,
                StringComparison.Ordinal) ||
            context.PayloadVersion !=
                VerifyTenantTerminationPayload.PayloadVersion ||
            context.CorrelationId != payload.ProcessId)
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }
    }

    private static InvalidOperationException InvalidProof() =>
        new("DataRights.TenantTerminationVerificationProofInvalid");

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
