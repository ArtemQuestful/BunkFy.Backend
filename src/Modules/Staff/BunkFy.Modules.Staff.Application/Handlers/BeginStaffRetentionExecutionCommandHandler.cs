namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Errors;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;

internal sealed class BeginStaffRetentionExecutionCommandHandler(
    IStaffRetentionExecutionRepository executions,
    IScopeContext scopeContext,
    IIdGenerator ids)
    : ICommandHandler<
        BeginStaffRetentionExecutionCommand,
        StaffRetentionExecutionStart>
{
    public async Task<Result<StaffRetentionExecutionStart>>
        HandleAsync(
            BeginStaffRetentionExecutionCommand command,
            CancellationToken cancellationToken)
    {
        RetentionContributionRequest request = command.Request;
        if (!IsExpectedRequest(request, scopeContext))
        {
            return Result.Failure<StaffRetentionExecutionStart>(
                StaffDomainErrors.RetentionExecutionCoordinateInvalid);
        }

        StaffRetentionExecution? execution =
            await executions.GetExecutionAsync(
                request.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            StaffRetentionSweepCheckpoint? checkpoint =
                await executions.GetCheckpointAsync(
                    StaffRetentionCoordinates.DataClassKey,
                    request.ExecutionPolicyVersion,
                    cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                Result<StaffRetentionSweepCheckpoint> created =
                    StaffRetentionSweepCheckpoint.Create(
                        ids.NewId(),
                        request.TenantId,
                        StaffRetentionCoordinates.DataClassKey,
                        request.ExecutionPolicyVersion,
                        request.StartedAtUtc);
                if (created.IsFailure)
                {
                    return Result.Failure<
                        StaffRetentionExecutionStart>(
                        created.Error);
                }

                checkpoint = created.Value;
                await executions.AddCheckpointAsync(
                    checkpoint,
                    cancellationToken).ConfigureAwait(false);
            }

            Result<StaffRetentionExecution> started =
                StaffRetentionExecution.Start(
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
                    StaffRetentionExecutionStart>(
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
            return Result.Failure<StaffRetentionExecutionStart>(
                StaffDomainErrors.RetentionExecutionCoordinateInvalid);
        }
        else if (
            (execution.State is
                StaffRetentionExecutionState.Running or
                StaffRetentionExecutionState.Failed) &&
            request.Attempt > execution.Attempt)
        {
            Result retryable = execution.ValidateRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retryable.IsFailure)
            {
                return Result.Failure<
                    StaffRetentionExecutionStart>(
                    retryable.Error);
            }

            if (execution.State ==
                StaffRetentionExecutionState.Failed)
            {
                StaffRetentionSweepCheckpoint? checkpoint =
                    await executions.GetCheckpointAsync(
                        StaffRetentionCoordinates.DataClassKey,
                        request.ExecutionPolicyVersion,
                        cancellationToken).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    return Result.Failure<
                        StaffRetentionExecutionStart>(
                        StaffApplicationErrors.RetentionProofConflict);
                }

                Result prepared = checkpoint.PrepareRetry(
                    execution,
                    request.StartedAtUtc);
                if (prepared.IsFailure)
                {
                    return Result.Failure<
                        StaffRetentionExecutionStart>(
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
                    StaffRetentionExecutionStart>(
                    retried.Error);
            }
        }
        else if (
            (execution.State is
                StaffRetentionExecutionState.Running or
                StaffRetentionExecutionState.Failed) &&
            request.Attempt != execution.Attempt)
        {
            return Result.Failure<StaffRetentionExecutionStart>(
                StaffDomainErrors.RetentionExecutionCoordinateInvalid);
        }

        bool dispatchRequired =
            execution.State == StaffRetentionExecutionState.Running;
        return Result.Success(
            new StaffRetentionExecutionStart(
                dispatchRequired,
                execution.StartingProjectionOrdinal,
                execution.AffectedCount,
                dispatchRequired ? null : ToResult(execution)));
    }

    internal static RetentionContributionResult ToResult(
        StaffRetentionExecution execution) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            execution.State switch
            {
                StaffRetentionExecutionState.Completed =>
                    RetentionContributionStatus.Completed,
                StaffRetentionExecutionState.Blocked =>
                    RetentionContributionStatus.Blocked,
                StaffRetentionExecutionState.Failed =>
                    RetentionContributionStatus.Failed,
                _ => throw new InvalidOperationException(
                    "Staff.RetentionExecutionNotTerminal")
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
            StaffRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            StaffRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        request.ExecutionPolicyVersion ==
            StaffRetentionCoordinates.ExecutionPolicyVersion;
}
