namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class InspectStaffIdentityProvisioningAnchorsQueryHandler(
    StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator,
    IScopeContext scopeContext)
    : IQueryHandler<InspectStaffIdentityProvisioningAnchorsQuery,
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidateInspection>>
{
    public Task<Result<IReadOnlyList<
        StaffIdentityProvisioningAnchorCandidateInspection>>> HandleAsync(
            InspectStaffIdentityProvisioningAnchorsQuery query,
            CancellationToken cancellationToken) =>
        !scopeContext.IsEnabled ||
        string.IsNullOrWhiteSpace(scopeContext.ScopeId)
            ? Task.FromResult(Result.Failure<IReadOnlyList<
                StaffIdentityProvisioningAnchorCandidateInspection>>(
                    StaffApplicationErrors.TenantRequired))
            : coordinator.InspectAsync(query.Candidates, cancellationToken);
}
