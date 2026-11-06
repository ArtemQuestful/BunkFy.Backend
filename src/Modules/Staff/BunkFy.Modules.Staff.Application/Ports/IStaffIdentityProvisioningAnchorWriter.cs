namespace BunkFy.Modules.Staff.Application.Ports;

public interface IStaffIdentityProvisioningAnchorWriter
{
    Task<StaffIdentityProvisioningAnchorRecord> AddAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        CancellationToken cancellationToken);
}
