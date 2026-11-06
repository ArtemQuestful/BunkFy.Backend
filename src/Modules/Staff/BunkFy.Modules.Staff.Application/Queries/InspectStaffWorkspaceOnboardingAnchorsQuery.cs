namespace BunkFy.Modules.Staff.Application.Queries;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record InspectStaffIdentityProvisioningAnchorsQuery(
    IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> Candidates)
    : IQuery<IReadOnlyList<StaffIdentityProvisioningAnchorCandidateInspection>>;
