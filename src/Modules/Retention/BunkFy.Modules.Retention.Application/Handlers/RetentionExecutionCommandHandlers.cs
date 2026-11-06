namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class BeginRetentionExecutionCommandHandler(
    RetentionExecutionMutationCoordinator mutations,
    IRetentionExecutionRepository executions,
    IRetentionScheduleStateRepository scheduleStates)
    : ICommandHandler<BeginRetentionExecutionCommand, RetentionExecutionStart>
{
    public async Task<Result<RetentionExecutionStart>> HandleAsync(
        BeginRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        RetentionExecutionTargetKind targetKind = Map(command.TargetScopeKind);
        if (!IsValidTarget(targetKind, command.PropertyId))
        {
            return Result.Failure<RetentionExecutionStart>(
                RetentionApplicationErrors.TargetUnavailable);
        }

        RetentionExecutionStartLease lease = await mutations.AcquireStartAsync(
                command,
                cancellationToken)
            .ConfigureAwait(false);
        if (!lease.TargetAvailable)
        {
            return Result.Failure<RetentionExecutionStart>(
                RetentionApplicationErrors.TargetUnavailable);
        }

        if (!lease.ScheduleAvailable)
        {
            return Result.Failure<RetentionExecutionStart>(
                RetentionApplicationErrors.ExecutionConflict);
        }

        RetentionExecution? current = lease.Execution;
        bool attemptAdvanced = false;
        if (current is null)
        {
            Result<RetentionExecution> started = RetentionExecution.Start(
                command.ExecutionId,
                command.TenantId,
                command.OwnerKey,
                command.DataClassKey,
                targetKind,
                command.PropertyId,
                command.ExecutionPolicyVersion,
                command.Attempt,
                command.StartedAtUtc,
                command.DeadlineUtc);
            if (started.IsFailure)
            {
                return Result.Failure<RetentionExecutionStart>(started.Error);
            }

            current = started.Value;
            await executions.AddAsync(current, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!current.MatchesCoordinate(
                    command.OwnerKey,
                    command.DataClassKey,
                    targetKind,
                    command.PropertyId,
                    command.ExecutionPolicyVersion))
            {
                return Result.Failure<RetentionExecutionStart>(
                    RetentionApplicationErrors.ExecutionConflict);
            }

            bool retryableState = current.State is
                RetentionExecutionState.Running or
                RetentionExecutionState.Failed;
            if (retryableState &&
                command.Attempt > current.Attempt)
            {
                Result retried = current.BeginRetry(
                    command.Attempt,
                    command.StartedAtUtc,
                    command.DeadlineUtc);
                if (retried.IsFailure)
                {
                    return Result.Failure<RetentionExecutionStart>(retried.Error);
                }

                attemptAdvanced = true;
            }
            else if (retryableState &&
                     command.Attempt != current.Attempt)
            {
                return Result.Failure<RetentionExecutionStart>(
                    RetentionApplicationErrors.ExecutionConflict);
            }
        }

        if (current.State == RetentionExecutionState.Running)
        {
            await scheduleStates.RecordStartedAsync(
                current,
                command.NextDueAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new RetentionExecutionStart(
            current.State == RetentionExecutionState.Running,
            attemptAdvanced,
            current.State,
            ToRequest(current)));
    }

    private static RetentionContributionRequest ToRequest(
        RetentionExecution execution) => new(
            RetentionExecutionContract.CurrentVersion,
            execution.Id,
            execution.ScopeId,
            execution.PropertyId,
            execution.OwnerKey,
            execution.DataClassKey,
            execution.ExecutionPolicyVersion,
            execution.Attempt,
            execution.StartedAtUtc,
            execution.DeadlineUtc);

    private static RetentionExecutionTargetKind Map(
        RetentionTargetScopeKind targetKind) =>
        targetKind switch
        {
            RetentionTargetScopeKind.Tenant => RetentionExecutionTargetKind.Tenant,
            RetentionTargetScopeKind.Property => RetentionExecutionTargetKind.Property,
            _ => RetentionExecutionTargetKind.Unknown
        };

    private static bool IsValidTarget(
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId) => targetKind switch
        {
            RetentionExecutionTargetKind.Tenant => propertyId is null,
            RetentionExecutionTargetKind.Property =>
                propertyId is not null && propertyId != Guid.Empty,
            _ => false
        };
}

internal sealed class CompleteRetentionExecutionCommandHandler(
    RetentionExecutionMutationCoordinator mutations,
    IRetentionScheduleStateRepository scheduleStates)
    : ICommandHandler<CompleteRetentionExecutionCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        CompleteRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        RetentionExecution? execution = await mutations.AcquireCompletionAsync(
                command.ExecutionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (execution is null)
        {
            return Result.Failure<Unit>(
                RetentionApplicationErrors.ExecutionNotFound);
        }

        RetentionExecutionState state = command.Result.Status switch
        {
            RetentionContributionStatus.Completed => RetentionExecutionState.Completed,
            RetentionContributionStatus.Blocked => RetentionExecutionState.Blocked,
            RetentionContributionStatus.Failed => RetentionExecutionState.Failed,
            _ => RetentionExecutionState.Unknown
        };
        Result completed = execution.Complete(
            state,
            command.Attempt,
            command.Result.ScannedCount,
            command.Result.AffectedCount,
            command.Result.RemainingCount,
            command.Result.OutcomeCode,
            command.Result.CompletedAtUtc,
            command.Result.HoldReviewDueAtUtc);
        if (completed.IsFailure)
        {
            return Result.Failure<Unit>(completed.Error);
        }

        await scheduleStates.RecordCompletedAsync(
            execution,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(Unit.Value);
    }
}
