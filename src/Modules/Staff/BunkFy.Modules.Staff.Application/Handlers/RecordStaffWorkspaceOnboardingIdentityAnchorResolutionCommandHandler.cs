namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class RecordStaffWorkspaceOnboardingIdentityAnchorResolutionCommandHandler(
    StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator,
    IScopeContext scopeContext)
    : ICommandHandler<
        RecordStaffWorkspaceOnboardingIdentityAnchorResolutionCommand,
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>
{
    public Task<Result<
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>> HandleAsync(
            RecordStaffWorkspaceOnboardingIdentityAnchorResolutionCommand
                command,
            CancellationToken cancellationToken) =>
        !scopeContext.IsEnabled ||
        string.IsNullOrWhiteSpace(scopeContext.ScopeId)
            ? Task.FromResult(Result.Failure<
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>(
                    StaffApplicationErrors.TenantRequired))
            : coordinator.RecordAsync(
                scopeContext.ScopeId,
                command.Request,
                cancellationToken);
}
