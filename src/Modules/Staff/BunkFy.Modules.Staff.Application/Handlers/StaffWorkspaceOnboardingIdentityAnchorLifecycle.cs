namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class StaffWorkspaceOnboardingIdentityAnchorLifecycle(
    IRequestDispatcher dispatcher)
    : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
      IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
{
    public async Task<IReadOnlyList<
        StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        Result<IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>>
            result = await dispatcher.QueryAsync(
                new ReadStaffWorkspaceOnboardingIdentityAnchorOutcomesQuery(
                    requests),
                cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Code);
    }

    public async Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult>
        RecordAsync(
            StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus> result =
            await dispatcher.SendAsync(
                new
                    RecordStaffWorkspaceOnboardingIdentityAnchorResolutionCommand(
                        request),
                cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new(result.Value)
            : throw new InvalidOperationException(result.Error.Code);
    }
}
