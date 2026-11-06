namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class StaffIdentityProvisioningAnchorCutover(
    IRequestDispatcher dispatcher)
    : IStaffIdentityProvisioningAnchorCutover
{
    public async Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Result<IReadOnlyList<StaffIdentityProvisioningAnchorCandidateInspection>>
            result = await dispatcher.QueryAsync(
                new InspectStaffIdentityProvisioningAnchorsQuery(candidates),
                cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new(true, result.Value, null)
            : new(false, [], result.Error.Code);
    }

    public async Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Result<StaffIdentityProvisioningAnchorApplySummary> result =
            await dispatcher.SendAsync(
                new ApplyStaffIdentityProvisioningAnchorsCommand(candidates),
                cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new(
                true,
                result.Value.AppliedCount,
                result.Value.AlreadyAnchoredCount,
                null)
            : new(false, 0, 0, result.Error.Code);
    }
}
