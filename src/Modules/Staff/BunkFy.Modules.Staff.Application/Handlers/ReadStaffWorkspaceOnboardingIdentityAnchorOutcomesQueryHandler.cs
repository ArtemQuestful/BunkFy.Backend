namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class ReadStaffWorkspaceOnboardingIdentityAnchorOutcomesQueryHandler(
    StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator,
    IScopeContext scopeContext)
    : IQueryHandler<
        ReadStaffWorkspaceOnboardingIdentityAnchorOutcomesQuery,
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>>
{
    public Task<Result<IReadOnlyList<
        StaffWorkspaceOnboardingIdentityAnchorOutcome>>> HandleAsync(
            ReadStaffWorkspaceOnboardingIdentityAnchorOutcomesQuery query,
            CancellationToken cancellationToken) =>
        !scopeContext.IsEnabled ||
        string.IsNullOrWhiteSpace(scopeContext.ScopeId)
            ? Task.FromResult(Result.Failure<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    StaffApplicationErrors.TenantRequired))
            : coordinator.ReadAsync(
                query.Requests,
                cancellationToken);
}
