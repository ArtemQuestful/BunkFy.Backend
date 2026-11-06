namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;

internal sealed class BeginGuestRetentionExecutionCommandHandler(
    IGuestRetentionExecutionRepository executions,
    IScopeContext scopeContext,
    IIdGenerator ids)
    : ICommandHandler<
        BeginGuestRetentionExecutionCommand,
        GuestRetentionExecutionStart>
{
    public async Task<Result<GuestRetentionExecutionStart>> HandleAsync(
        BeginGuestRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        RetentionContributionRequest request = command.Request;
        if (!IsExpectedRequest(request, scopeContext))
        {
            return Result.Failure<GuestRetentionExecutionStart>(
                Domain.Errors.GuestsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }

        GuestRetentionExecution? execution =
            await executions.GetExecutionAsync(
                request.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            GuestRetentionSweepCheckpoint? checkpoint =
                await executions.GetCheckpointAsync(
                    GuestRetentionCoordinates.DataClassKey,
                    cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                Result<GuestRetentionSweepCheckpoint> created =
                    GuestRetentionSweepCheckpoint.Create(
                        ids.NewId(),
                        request.TenantId,
                        GuestRetentionCoordinates.DataClassKey,
                        request.StartedAtUtc);
                if (created.IsFailure)
                {
                    return Result.Failure<GuestRetentionExecutionStart>(
                        created.Error);
                }

                checkpoint = created.Value;
                await executions.AddCheckpointAsync(
                    checkpoint,
                    cancellationToken).ConfigureAwait(false);
            }

            Result<GuestRetentionExecution> started =
                GuestRetentionExecution.Start(
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
                return Result.Failure<GuestRetentionExecutionStart>(
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
            return Result.Failure<GuestRetentionExecutionStart>(
                Domain.Errors.GuestsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }
        else if (
            (execution.State is
                GuestRetentionExecutionState.Running or
                GuestRetentionExecutionState.Failed) &&
            request.Attempt > execution.Attempt)
        {
            Result retryable = execution.ValidateRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retryable.IsFailure)
            {
                return Result.Failure<GuestRetentionExecutionStart>(
                    retryable.Error);
            }

            if (execution.State == GuestRetentionExecutionState.Failed)
            {
                GuestRetentionSweepCheckpoint? checkpoint =
                    await executions.GetCheckpointAsync(
                        GuestRetentionCoordinates.DataClassKey,
                        cancellationToken).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    return Result.Failure<GuestRetentionExecutionStart>(
                        GuestsApplicationErrors.RetentionProofConflict);
                }

                Result prepared = checkpoint.PrepareRetry(
                    execution,
                    request.StartedAtUtc);
                if (prepared.IsFailure)
                {
                    return Result.Failure<GuestRetentionExecutionStart>(
                        prepared.Error);
                }
            }

            Result retried = execution.BeginRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retried.IsFailure)
            {
                return Result.Failure<GuestRetentionExecutionStart>(
                    retried.Error);
            }
        }
        else if (
            (execution.State is
                GuestRetentionExecutionState.Running or
                GuestRetentionExecutionState.Failed) &&
            request.Attempt != execution.Attempt)
        {
            return Result.Failure<GuestRetentionExecutionStart>(
                Domain.Errors.GuestsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }

        bool dispatchRequired =
            execution.State == GuestRetentionExecutionState.Running;
        return Result.Success(new GuestRetentionExecutionStart(
            dispatchRequired,
            execution.StartingProjectionOrdinal,
            execution.AffectedCount,
            dispatchRequired ? null : ToResult(execution)));
    }

    private static bool IsExpectedRequest(
        RetentionContributionRequest request,
        IScopeContext scopeContext) =>
        scopeContext.IsEnabled &&
        string.Equals(
            scopeContext.ScopeId,
            request.TenantId,
            StringComparison.Ordinal) &&
        request.ContractVersion == RetentionExecutionContract.CurrentVersion &&
        request.PropertyId is null &&
        request.ExecutionId != Guid.Empty &&
        string.Equals(
            request.OwnerKey,
            GuestRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            GuestRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        request.ExecutionPolicyVersion ==
            GuestRetentionCoordinates.ExecutionPolicyVersion;

    internal static RetentionContributionResult ToResult(
        GuestRetentionExecution execution) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            execution.State switch
            {
                GuestRetentionExecutionState.Completed =>
                    RetentionContributionStatus.Completed,
                GuestRetentionExecutionState.Blocked =>
                    RetentionContributionStatus.Blocked,
                GuestRetentionExecutionState.Failed =>
                    RetentionContributionStatus.Failed,
                _ => throw new InvalidOperationException(
                    "Guests.RetentionExecutionNotTerminal")
            },
            execution.ScannedCount!.Value,
            execution.AffectedCount,
            execution.RemainingCount!.Value,
            execution.OutcomeCode!,
            execution.CompletedAtUtc!.Value,
            execution.HoldReviewDueAtUtc);
}

internal sealed class CompleteGuestRetentionExecutionCommandHandler(
    IGuestRetentionExecutionRepository executions)
    : ICommandHandler<
        CompleteGuestRetentionExecutionCommand,
        RetentionContributionResult>
{
    public async Task<Result<RetentionContributionResult>> HandleAsync(
        CompleteGuestRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        GuestRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return Result.Failure<RetentionContributionResult>(
                GuestsApplicationErrors.RetentionExecutionNotFound);
        }

        GuestRetentionSweepCheckpoint? checkpoint =
            await executions.GetCheckpointAsync(
                GuestRetentionCoordinates.DataClassKey,
                cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return Result.Failure<RetentionContributionResult>(
                GuestsApplicationErrors.RetentionProofConflict);
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

        if (command.State == GuestRetentionExecutionState.Failed)
        {
            return Result.Success(
                BeginGuestRetentionExecutionCommandHandler.ToResult(
                    execution));
        }

        Result advanced = checkpoint.Advance(
            command.ExpectedAfterProjectionOrdinal,
            command.NextAfterProjectionOrdinal,
            command.ExecutionId,
            command.CompletedAtUtc);
        return advanced.IsFailure
            ? Result.Failure<RetentionContributionResult>(advanced.Error)
            : Result.Success(
                BeginGuestRetentionExecutionCommandHandler.ToResult(
                    execution));
    }
}
