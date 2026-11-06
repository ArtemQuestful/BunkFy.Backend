namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class BeginWorkspaceStaffOnboardingRetentionExecutionCommandHandler(
    IWorkspaceStaffOnboardingRetentionExecutionRepository executions,
    WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator,
    IScopeContext scopeContext)
    : ICommandHandler<
        BeginWorkspaceStaffOnboardingRetentionExecutionCommand,
        WorkspaceStaffOnboardingRetentionExecutionStart>
{
    public async Task<Result<WorkspaceStaffOnboardingRetentionExecutionStart>>
        HandleAsync(
            BeginWorkspaceStaffOnboardingRetentionExecutionCommand command,
            CancellationToken cancellationToken)
    {
        RetentionContributionRequest request = command.Request;
        if (!IsExpectedRequest(request, scopeContext))
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionStart>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        Result<WorkspaceStaffOnboardingRetentionExecutionAcquisition> acquired =
            await coordinator.AcquireAsync(
                request.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionStart>(
                    acquired.Error);
        }

        WorkspaceStaffOnboardingRetentionExecution? execution =
            acquired.Value.Execution;
        if (execution is null)
        {
            Result<WorkspaceStaffOnboardingRetentionExecution> started =
                WorkspaceStaffOnboardingRetentionExecution.Start(
                    request.ExecutionId,
                    request.TenantId,
                    request.DataClassKey,
                    request.ExecutionPolicyVersion,
                    request.Attempt,
                    request.StartedAtUtc,
                    request.DeadlineUtc);
            if (started.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffOnboardingRetentionExecutionStart>(
                        started.Error);
            }

            execution = started.Value;
            await executions.AddAsync(execution, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!execution.MatchesCoordinate(
                     request.DataClassKey,
                     request.ExecutionPolicyVersion))
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionStart>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }
        else if (request.Attempt == execution.Attempt &&
                 !execution.MatchesAttemptWindow(
                     request.Attempt,
                     request.StartedAtUtc,
                     request.DeadlineUtc))
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionStart>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }
        else if (request.Attempt > execution.Attempt &&
                 execution.State is
                     WorkspaceStaffOnboardingRetentionExecutionState.Running or
                     WorkspaceStaffOnboardingRetentionExecutionState.Failed)
        {
            Result retry = execution.BeginRetry(
                request.Attempt,
                request.StartedAtUtc,
                request.DeadlineUtc);
            if (retry.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffOnboardingRetentionExecutionStart>(
                        retry.Error);
            }
        }

        if (execution.Attempt != request.Attempt)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionStart>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        bool dispatchRequired = execution.State ==
            WorkspaceStaffOnboardingRetentionExecutionState.Running;
        return Result.Success(
            new WorkspaceStaffOnboardingRetentionExecutionStart(
                dispatchRequired,
                execution.ScannedCount,
                execution.AffectedCount,
                dispatchRequired ? null : ToResult(execution)));
    }

    internal static RetentionContributionResult ToResult(
        WorkspaceStaffOnboardingRetentionExecution execution) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            execution.State switch
            {
                WorkspaceStaffOnboardingRetentionExecutionState.Completed =>
                    RetentionContributionStatus.Completed,
                WorkspaceStaffOnboardingRetentionExecutionState.Failed =>
                    RetentionContributionStatus.Failed,
                _ => throw new InvalidOperationException(
                    "Workspaces.StaffOnboardingRetentionExecutionNotTerminal")
            },
            execution.ScannedCount,
            execution.AffectedCount,
            execution.RemainingCount!.Value,
            execution.OutcomeCode!,
            execution.CompletedAtUtc!.Value);

    private static bool IsExpectedRequest(
        RetentionContributionRequest request,
        IScopeContext scopeContext) =>
        request.ContractVersion == RetentionExecutionContract.CurrentVersion &&
        request.ExecutionId != Guid.Empty &&
        request.PropertyId is null &&
        request.Attempt > 0 &&
        request.StartedAtUtc != default &&
        request.DeadlineUtc > request.StartedAtUtc &&
        request.ExecutionPolicyVersion ==
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion &&
        string.Equals(
            request.OwnerKey,
            WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        scopeContext.IsEnabled &&
        string.Equals(
            scopeContext.ScopeId?.Trim(),
            request.TenantId?.Trim(),
            StringComparison.Ordinal) &&
        Guid.TryParse(request.TenantId, out _);
}

internal sealed class ListWorkspaceStaffOnboardingRetentionCandidatesCommandHandler(
    IWorkspaceStaffOnboardingRetentionRepository candidates,
    WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator)
    : ICommandHandler<
        ListWorkspaceStaffOnboardingRetentionCandidatesCommand,
        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>
{
    public async Task<Result<IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>>
        HandleAsync(
            ListWorkspaceStaffOnboardingRetentionCandidatesCommand command,
            CancellationToken cancellationToken)
    {
        if (command.SourceExpiredBeforeUtc == default ||
            command.MaximumCount is < 1 or > 501)
        {
            return Result.Failure<
                IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        Result<WorkspaceStaffOnboardingRetentionExecution> execution =
            await coordinator.AcquireRunningAsync(
                command.ExecutionId,
                command.Attempt,
                cancellationToken).ConfigureAwait(false);
        if (execution.IsFailure)
        {
            return Result.Failure<
                IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>(
                    execution.Error);
        }

        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate> result =
            await candidates.ListEligibleAsync(
                execution.Value.ScopeId,
                command.SourceExpiredBeforeUtc,
                command.MaximumCount,
                cancellationToken).ConfigureAwait(false);
        return Result.Success(result);
    }
}

internal sealed class CompleteWorkspaceStaffOnboardingRetentionExecutionCommandHandler(
    WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator)
    : ICommandHandler<
        CompleteWorkspaceStaffOnboardingRetentionExecutionCommand,
        RetentionContributionResult>
{
    public async Task<Result<RetentionContributionResult>> HandleAsync(
        CompleteWorkspaceStaffOnboardingRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        Result<WorkspaceStaffOnboardingRetentionExecutionAcquisition> acquired =
            await coordinator.AcquireAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<RetentionContributionResult>(acquired.Error);
        }

        WorkspaceStaffOnboardingRetentionExecution? execution =
            acquired.Value.Execution;
        if (execution is null)
        {
            return Result.Failure<RetentionContributionResult>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RetentionExecutionNotFound);
        }

        Result completed = execution.Complete(
            command.State,
            command.Attempt,
            command.ScannedCount,
            command.RemainingCount,
            command.OutcomeCode,
            command.CompletedAtUtc);
        return completed.IsSuccess
            ? Result.Success(
                BeginWorkspaceStaffOnboardingRetentionExecutionCommandHandler
                    .ToResult(execution))
            : Result.Failure<RetentionContributionResult>(completed.Error);
    }
}
