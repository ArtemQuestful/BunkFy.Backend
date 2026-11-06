namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class
    CompleteReservationRetentionExecutionCommandHandler(
        IReservationRetentionExecutionRepository executions)
    : ICommandHandler<
        CompleteReservationRetentionExecutionCommand,
        RetentionContributionResult>
{
    public async Task<Result<RetentionContributionResult>> HandleAsync(
        CompleteReservationRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        ReservationRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return Result.Failure<RetentionContributionResult>(
                ReservationsApplicationErrors
                    .RetentionExecutionNotFound);
        }

        ReservationRetentionSweepCheckpoint? checkpoint =
            await executions.GetCheckpointAsync(
                ReservationRetentionCoordinates.DataClassKey,
                execution.ExecutionPolicyVersion,
                cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return Result.Failure<RetentionContributionResult>(
                ReservationsApplicationErrors.RetentionProofConflict);
        }

        DateTimeOffset completedAtUtc =
            ReservationMutationTime.Normalize(
                command.CompletedAtUtc);
        Result completed = execution.Complete(
            command.State,
            command.Attempt,
            command.ScannedCount,
            command.RemainingCount,
            command.OutcomeCode,
            completedAtUtc,
            command.HoldReviewDueAtUtc);
        if (completed.IsFailure)
        {
            return Result.Failure<RetentionContributionResult>(
                completed.Error);
        }

        if (command.State ==
            ReservationRetentionExecutionState.Failed)
        {
            return Result.Success(
                BeginReservationRetentionExecutionCommandHandler
                    .ToResult(execution));
        }

        Result advanced = checkpoint.Advance(
            command.ExpectedAfterProjectionOrdinal,
            command.NextAfterProjectionOrdinal,
            command.ExecutionId,
            completedAtUtc);
        return advanced.IsFailure
            ? Result.Failure<RetentionContributionResult>(
                advanced.Error)
            : Result.Success(
                BeginReservationRetentionExecutionCommandHandler
                    .ToResult(execution));
    }
}
