namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;

internal sealed class StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
    Func<
        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest,
        StaffWorkspaceOnboardingIdentityAnchorOutcome>? resolve = null)
    : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
{
    public Task<IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>>
        ReadAsync(
            IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest> requests,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count is < 1 or >
            StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits
                .MaximumBatchSize ||
            requests.Select(request => request.ApplicationId).Distinct().Count() !=
                requests.Count)
        {
            throw new ArgumentException(
                "The Staff identity-anchor outcome request is invalid.",
                nameof(requests));
        }

        StaffWorkspaceOnboardingIdentityAnchorOutcome[] outcomes = requests
            .Select(request => resolve?.Invoke(request) ?? Absent(request))
            .ToArray();
        return Task.FromResult<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>>(outcomes);
    }

    public static StaffWorkspaceOnboardingIdentityAnchorOutcome Absent(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request) => new(
            request.ApplicationId,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
            StaffMemberId: null,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null);
}
