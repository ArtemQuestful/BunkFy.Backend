namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed record WorkspaceStaffOnboardingRetentionExecutionAcquisition(
    WorkspaceStaffOnboardingRetentionExecution? Execution);

internal sealed class WorkspaceStaffOnboardingRetentionExecutionCoordinator(
    IWorkspaceStaffOnboardingRetentionExecutionLock executionLock,
    IWorkspaceStaffOnboardingRetentionExecutionRepository executions,
    IScopeContext scopeContext)
{
    public async Task<Result<
        WorkspaceStaffOnboardingRetentionExecutionAcquisition>>
        AcquireAsync(
            Guid executionId,
            CancellationToken cancellationToken)
    {
        string tenantId = this.GetTenantId();
        if (tenantId.Length == 0 || executionId == Guid.Empty)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionAcquisition>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        await executionLock.AcquireAsync(
            tenantId,
            executionId,
            cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboardingRetentionExecution? execution =
            await executions.GetAsync(executionId, cancellationToken)
                .ConfigureAwait(false);
        if (execution is not null &&
            !string.Equals(
                execution.ScopeId,
                tenantId,
                StringComparison.Ordinal))
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecutionAcquisition>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        return Result.Success(
            new WorkspaceStaffOnboardingRetentionExecutionAcquisition(
                execution));
    }

    public async Task<Result<WorkspaceStaffOnboardingRetentionExecution>>
        AcquireRunningAsync(
            Guid executionId,
            int attempt,
            CancellationToken cancellationToken)
    {
        if (attempt < 1)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecution>(
                    WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        Result<WorkspaceStaffOnboardingRetentionExecutionAcquisition> acquired =
            await this.AcquireAsync(executionId, cancellationToken)
                .ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecution>(acquired.Error);
        }

        WorkspaceStaffOnboardingRetentionExecution? execution =
            acquired.Value.Execution;
        if (execution is null)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionExecution>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .RetentionExecutionNotFound);
        }

        return execution.State ==
                WorkspaceStaffOnboardingRetentionExecutionState.Running &&
            execution.Attempt == attempt &&
            execution.MatchesCoordinate(
                WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
                WorkspaceStaffOnboardingRetentionCoordinates
                    .ExecutionPolicyVersion)
                ? Result.Success(execution)
                : Result.Failure<
                    WorkspaceStaffOnboardingRetentionExecution>(
                        WorkspaceStaffRetentionErrors
                            .ExecutionCoordinateInvalid);
    }

    private string GetTenantId() => scopeContext.IsEnabled
        ? scopeContext.ScopeId?.Trim() ?? string.Empty
        : string.Empty;
}
