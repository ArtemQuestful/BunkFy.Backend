namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class StaffOnboardingProvisioner(
    IRequestDispatcher dispatcher,
    IStaffIdentityProvisioningAnchorRepository anchors)
    : IStaffOnboardingProvisioner
{
    public async Task<StaffOnboardingProvisioningResult> ProvisionAsync(
        StaffOnboardingProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<StaffMemberDto> result = await dispatcher.SendAsync(
            new ProvisionStaffOnboardingCommand(
                request.OperationId,
                request.AuthSubjectId,
                request.DisplayName,
                request.LegalName,
                request.WorkEmail,
                request.WorkPhone,
                request.EmployeeNumber,
                request.JobTitle,
                request.Department,
                request.ActorId),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return new StaffOnboardingProvisioningResult(
                false,
                null,
                result.Error.Code);
        }

        StaffIdentityProvisioningAnchorRecord? anchor = await anchors.GetAsync(
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (anchor is null ||
            anchor.StaffMemberId != result.Value.StaffMemberId ||
            !anchor.ResolutionEventId.HasValue)
        {
            return new StaffOnboardingProvisioningResult(
                false,
                null,
                StaffApplicationErrors.OnboardingReplayUnavailable.Code);
        }

        return new StaffOnboardingProvisioningResult(
            true,
            result.Value.StaffMemberId,
            null,
            anchor.ResolutionEventId.Value);
    }
}
