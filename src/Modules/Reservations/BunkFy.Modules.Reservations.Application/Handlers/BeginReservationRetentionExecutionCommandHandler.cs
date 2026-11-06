namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;

internal sealed class
    BeginReservationRetentionExecutionCommandHandler(
        IReservationRetentionExecutionRepository executions,
        IScopeContext scopeContext,
        IIdGenerator ids)
    : ICommandHandler<
        BeginReservationRetentionExecutionCommand,
        ReservationRetentionExecutionStart>
{
    public async Task<Result<ReservationRetentionExecutionStart>>
        HandleAsync(
            BeginReservationRetentionExecutionCommand command,
            CancellationToken cancellationToken)
    {
        RetentionContributionRequest request = command.Request;
        if (!IsExpectedRequest(request, scopeContext))
        {
            return Result.Failure<
                ReservationRetentionExecutionStart>(
                ReservationsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }

        ReservationRetentionExecution? execution =
            await executions.GetExecutionAsync(
                request.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            ReservationRetentionSweepCheckpoint? checkpoint =
                await executions.GetCheckpointAsync(
                    ReservationRetentionCoordinates.DataClassKey,
                    request.ExecutionPolicyVersion,
                    cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                Result<ReservationRetentionSweepCheckpoint>
                    created =
                    ReservationRetentionSweepCheckpoint.Create(
                        ids.NewId(),
                        request.TenantId,
                        ReservationRetentionCoordinates.DataClassKey,
                        request.ExecutionPolicyVersion,
                        request.StartedAtUtc);
                if (created.IsFailure)
                {
                    return Result.Failure<
                        ReservationRetentionExecutionStart>(
                        created.Error);
                }

                checkpoint = created.Value;
                await executions.AddCheckpointAsync(
                    checkpoint,
                    cancellationToken).ConfigureAwait(false);
            }

            Result<ReservationRetentionExecution> started =
                ReservationRetentionExecution.Start(
                    request.ExecutionId,
                    request.TenantId,
                    request.DataClassKey,
                    request.ExecutionPolicyVersion,
                    request.Attempt,
                    checkpoint.AfterProjectionOrdinal,
                    request.StartedAtUtc,
                    request.DeadlineUtc);
            if (started.IsFailure)
            {
                return Result.Failure<
                    ReservationRetentionExecutionStart>(
                    started.Error);
            }

            execution = started.Value;
            await executions.AddExecutionAsync(
                execution,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!execution.MatchesCoordinate(
                     request.DataClassKey,
                     request.ExecutionPolicyVersion))
        {
            return Result.Failure<
                ReservationRetentionExecutionStart>(
                ReservationsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }
        else if (
            (execution.State is
                ReservationRetentionExecutionState.Running or
                ReservationRetentionExecutionState.Failed) &&
            request.Attempt > execution.Attempt)
        {
            Result retryable = execution.ValidateRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retryable.IsFailure)
            {
                return Result.Failure<
                    ReservationRetentionExecutionStart>(
                    retryable.Error);
            }

            if (execution.State ==
                ReservationRetentionExecutionState.Failed)
            {
                ReservationRetentionSweepCheckpoint? checkpoint =
                    await executions.GetCheckpointAsync(
                        ReservationRetentionCoordinates.DataClassKey,
                        request.ExecutionPolicyVersion,
                        cancellationToken).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    return Result.Failure<
                        ReservationRetentionExecutionStart>(
                        ReservationsApplicationErrors
                            .RetentionProofConflict);
                }

                Result prepared = checkpoint.PrepareRetry(
                    execution,
                    request.StartedAtUtc);
                if (prepared.IsFailure)
                {
                    return Result.Failure<
                        ReservationRetentionExecutionStart>(
                        prepared.Error);
                }
            }

            Result retried = execution.BeginRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retried.IsFailure)
            {
                return Result.Failure<
                    ReservationRetentionExecutionStart>(
                    retried.Error);
            }
        }
        else if (
            (execution.State is
                ReservationRetentionExecutionState.Running or
                ReservationRetentionExecutionState.Failed) &&
            request.Attempt != execution.Attempt)
        {
            return Result.Failure<
                ReservationRetentionExecutionStart>(
                ReservationsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }

        bool dispatchRequired =
            execution.State ==
                ReservationRetentionExecutionState.Running;
        return Result.Success(
            new ReservationRetentionExecutionStart(
                dispatchRequired,
                execution.StartingProjectionOrdinal,
                execution.AffectedCount,
                dispatchRequired
                    ? null
                    : ToResult(execution)));
    }

    internal static RetentionContributionResult ToResult(
        ReservationRetentionExecution execution) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            execution.State switch
            {
                ReservationRetentionExecutionState.Completed =>
                    RetentionContributionStatus.Completed,
                ReservationRetentionExecutionState.Blocked =>
                    RetentionContributionStatus.Blocked,
                ReservationRetentionExecutionState.Failed =>
                    RetentionContributionStatus.Failed,
                _ => throw new InvalidOperationException(
                    "Reservations.RetentionExecutionNotTerminal")
            },
            execution.ScannedCount!.Value,
            execution.AffectedCount,
            execution.RemainingCount!.Value,
            execution.OutcomeCode!,
            execution.CompletedAtUtc!.Value,
            execution.HoldReviewDueAtUtc);

    private static bool IsExpectedRequest(
        RetentionContributionRequest request,
        IScopeContext scopeContext) =>
        scopeContext.IsEnabled &&
        string.Equals(
            scopeContext.ScopeId,
            request.TenantId,
            StringComparison.Ordinal) &&
        request.ContractVersion ==
            RetentionExecutionContract.CurrentVersion &&
        request.PropertyId is null &&
        request.ExecutionId != Guid.Empty &&
        string.Equals(
            request.OwnerKey,
            ReservationRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            ReservationRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        request.ExecutionPolicyVersion ==
            ReservationRetentionCoordinates.ExecutionPolicyVersion;
}
