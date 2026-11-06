namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;

// Persistence-only export fixtures contain no Workspaces onboarding records.
// An unexpected authority read must fail rather than manufacture Staff absence.
internal sealed class UnusedStaffOnboardingOutcomeReader
    : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
{
    public Task<IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest> requests,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "This persistence-only fixture has no Staff onboarding authority.");
}
