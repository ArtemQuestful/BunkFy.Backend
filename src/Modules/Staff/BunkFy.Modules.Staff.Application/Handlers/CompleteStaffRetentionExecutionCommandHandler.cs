namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class CompleteStaffRetentionExecutionCommandHandler(
    IStaffRetentionExecutionRepository executions)
    : ICommandHandler<
        CompleteStaffRetentionExecutionCommand,
        RetentionContributionResult>
{
    public async Task<Result<RetentionContributionResult>> HandleAsync(
        CompleteStaffRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        StaffRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return Result.Failure<RetentionContributionResult>(
                StaffApplicationErrors.RetentionExecutionNotFound);
        }

        StaffRetentionSweepCheckpoint? checkpoint =
            await executions.GetCheckpointAsync(
                StaffRetentionCoordinates.DataClassKey,
                execution.ExecutionPolicyVersion,
                cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return Result.Failure<RetentionContributionResult>(
                StaffApplicationErrors.RetentionProofConflict);
        }

        Result completed = execution.Complete(
            command.State,
            command.Attempt,
            command.ScannedCount,
            command.RemainingCount,
            command.OutcomeCode,
            command.CompletedAtUtc,
            command.HoldReviewDueAtUtc);
        if (completed.IsFailure)
        {
            return Result.Failure<RetentionContributionResult>(
                completed.Error);
        }

        if (command.State == StaffRetentionExecutionState.Failed)
        {
            return Result.Success(
                BeginStaffRetentionExecutionCommandHandler
                    .ToResult(execution));
        }

        Result advanced = checkpoint.Advance(
            command.ExpectedAfterProjectionOrdinal,
            command.NextAfterProjectionOrdinal,
            command.ExecutionId,
            command.CompletedAtUtc);
        return advanced.IsFailure
            ? Result.Failure<RetentionContributionResult>(
                advanced.Error)
            : Result.Success(
                BeginStaffRetentionExecutionCommandHandler
                    .ToResult(execution));
    }
}
