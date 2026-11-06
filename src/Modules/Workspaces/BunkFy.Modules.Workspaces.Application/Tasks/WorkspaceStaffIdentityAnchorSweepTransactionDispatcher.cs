namespace BunkFy.Modules.Workspaces.Application.Tasks;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Microsoft.Extensions.DependencyInjection;

internal sealed class WorkspaceStaffIdentityAnchorSweepTransactionDispatcher(
    IServiceScopeFactory scopeFactory)
    : IWorkspaceStaffIdentityAnchorSweepTransactionDispatcher
{
    public Task<Result<WorkspaceStaffIdentityAnchorSweepPage>>
        PreparePageAsync(
            TaskExecutionContext context,
            PrepareWorkspaceStaffIdentityAnchorSweepPageCommand command,
            CancellationToken cancellationToken) =>
        this.DispatchInFreshScopeAsync<
            PrepareWorkspaceStaffIdentityAnchorSweepPageCommand,
            WorkspaceStaffIdentityAnchorSweepPage>(
                context,
                command,
                cancellationToken);

    public Task<Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>>
        ReconcileCandidateAsync(
            TaskExecutionContext context,
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand command,
            CancellationToken cancellationToken) =>
        this.DispatchInFreshScopeAsync<
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand,
            WorkspaceStaffIdentityAnchorSweepCandidateResult>(
                context,
                command,
                cancellationToken);

    public Task<Result<Unit>> AdvanceAsync(
        TaskExecutionContext context,
        AdvanceWorkspaceStaffIdentityAnchorSweepCommand command,
        CancellationToken cancellationToken) =>
        this.DispatchInFreshScopeAsync<
            AdvanceWorkspaceStaffIdentityAnchorSweepCommand,
            Unit>(
                context,
                command,
                cancellationToken);

    private async Task<Result<TResponse>> DispatchInFreshScopeAsync<
        TCommand,
        TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
        where TCommand : ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);

        await using AsyncServiceScope scope =
            scopeFactory.CreateAsyncScope();
        IScopeContextAccessor scopeContext = scope.ServiceProvider
            .GetRequiredService<IScopeContextAccessor>();
        if (string.IsNullOrWhiteSpace(context.ScopeId))
        {
            return Invalid<TResponse>();
        }

        scopeContext.SetScope(context.ScopeId);
        try
        {
            if (!WorkspaceStaffIdentityAnchorTenantScope
                    .TryGetCanonicalTenantId(
                        scopeContext,
                        out string tenantId) ||
                !string.Equals(
                    tenantId,
                    context.ScopeId,
                    StringComparison.Ordinal))
            {
                return Invalid<TResponse>();
            }

            return await scope.ServiceProvider
                .GetRequiredService<ITaskCommandDispatcher>()
                .DispatchAsync<TCommand, TResponse>(
                    context,
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            scopeContext.ClearScope();
        }
    }

    private static Result<TResponse> Invalid<TResponse>() =>
        Result.Failure<TResponse>(
            WorkspaceStaffIdentityAnchorSweepErrors.InvalidTask);
}
