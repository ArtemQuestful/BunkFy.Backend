namespace BunkFy.Modules.Staff.Application.Commands;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record RecordStaffWorkspaceOnboardingIdentityAnchorResolutionCommand(
    StaffWorkspaceOnboardingIdentityAnchorResolutionRequest Request)
    : ITransactionalCommand<
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>;
