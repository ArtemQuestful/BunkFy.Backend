namespace BunkFy.Modules.Staff.Application.Queries;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record ReadStaffWorkspaceOnboardingIdentityAnchorOutcomesQuery(
    IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
        Requests)
    : IQuery<IReadOnlyList<
        StaffWorkspaceOnboardingIdentityAnchorOutcome>>;
