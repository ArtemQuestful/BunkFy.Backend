namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class ApplyStaffIdentityProvisioningAnchorsCommandHandler(
    StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator,
    IScopeContext scopeContext)
    : ICommandHandler<ApplyStaffIdentityProvisioningAnchorsCommand,
        StaffIdentityProvisioningAnchorApplySummary>
{
    public Task<Result<StaffIdentityProvisioningAnchorApplySummary>> HandleAsync(
        ApplyStaffIdentityProvisioningAnchorsCommand command,
        CancellationToken cancellationToken) =>
        !scopeContext.IsEnabled ||
        string.IsNullOrWhiteSpace(scopeContext.ScopeId)
            ? Task.FromResult(Result.Failure<
                StaffIdentityProvisioningAnchorApplySummary>(
                    StaffApplicationErrors.TenantRequired))
            : coordinator.ApplyAsync(
                scopeContext.ScopeId,
                command.Candidates,
                cancellationToken);
}
