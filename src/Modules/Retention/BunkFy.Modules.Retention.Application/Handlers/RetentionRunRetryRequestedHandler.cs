namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

[IntegrationEventHandler(RetentionModuleMetadata.RunRetryRequestedHandlerName)]
internal sealed class RetentionRunRetryRequestedHandler(
    IRetentionRunRetryRequestRepository requests,
    IRetentionMutationLock mutationLock,
    IRetentionScheduleHealthReader scheduleHealth,
    IRetentionRunRetryExecutor executor,
    ISystemClock clock)
    : IIntegrationEventHandler<RetentionRunRetryRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        RetentionRunRetryRequestedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        RetentionRunRetryRequest? request = await requests.GetAsync(
                integrationEvent.RequestId,
                cancellationToken)
            .ConfigureAwait(false);
        if (request is null ||
            request.RunId != integrationEvent.RunId ||
            request.Attempt != integrationEvent.Attempt ||
            request.State != RetentionRunRetryRequestState.Pending)
        {
            return;
        }

        await mutationLock.AcquireScheduleAsync(
                request.ScopeId,
                request.OwnerKey,
                request.DataClassKey,
                request.PropertyId,
                request.ExecutionPolicyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        RetentionScheduleStateSnapshot? current = await scheduleHealth.GetAsync(
                request.ScopeId,
                request.OwnerKey,
                request.DataClassKey,
                request.PropertyId,
                request.ExecutionPolicyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (!MatchesCurrentEvidence(request, current))
        {
            RequireTransition(request.MarkFailed(
                "schedule-evidence-changed",
                clock.UtcNow));
            return;
        }

        RetentionRunRetryExecutionOutcome outcome;
        try
        {
            outcome = await executor.ExecuteAsync(
                    ToWorkItem(request),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            RequireTransition(request.MarkFailed(
                "recovery-executor-unavailable",
                clock.UtcNow));
            return;
        }

        if (outcome.Status == RetentionRunRetryExecutionStatus.Applied)
        {
            RequireTransition(request.MarkApplied(clock.UtcNow));
            return;
        }

        RequireTransition(request.MarkFailed(
            outcome.FailureCode ?? "task-run-unavailable",
            clock.UtcNow));
    }

    private static bool MatchesCurrentEvidence(
        RetentionRunRetryRequest request,
        RetentionScheduleStateSnapshot? current) =>
        current is not null &&
        current.LastExecutionId == request.RunId &&
        current.Version == request.EvidenceVersion &&
        current.State == (int)RetentionExecutionState.Failed;

    private static RetentionRunRetryWorkItem ToWorkItem(
        RetentionRunRetryRequest request) => new(
            request.Id,
            request.RunId,
            request.ScopeId,
            request.OwnerKey,
            request.DataClassKey,
            request.TargetKind == RetentionExecutionTargetKind.Tenant
                ? RetentionTargetScopeKind.Tenant
                : RetentionTargetScopeKind.Property,
            request.PropertyId,
            request.ExecutionPolicyVersion,
            request.EvidenceVersion,
            request.Attempt,
            request.ScheduledAtUtc);

    private static void RequireTransition(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                "Retention recovery request transition failed.");
        }
    }
}

internal sealed class UnavailableRetentionRunRetryExecutor
    : IRetentionRunRetryExecutor
{
    public Task<RetentionRunRetryExecutionOutcome> ExecuteAsync(
        RetentionRunRetryWorkItem workItem,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            RetentionRunRetryExecutionOutcome.TransientFailure(
                "task-runtime-unavailable"));
}
