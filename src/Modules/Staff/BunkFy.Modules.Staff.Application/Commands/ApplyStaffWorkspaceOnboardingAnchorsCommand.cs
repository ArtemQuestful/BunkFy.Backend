namespace BunkFy.Modules.Staff.Application.Commands;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record ApplyStaffIdentityProvisioningAnchorsCommand(
    IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> Candidates)
    : ITransactionalCommand<StaffIdentityProvisioningAnchorApplySummary>,
        IStaffPersistenceRetryableCommand;
