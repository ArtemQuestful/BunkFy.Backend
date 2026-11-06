namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application.Ports;

internal static class IdentityProvisioningAnchorRecordId
{
    public static Guid Create(
        Guid staffMemberId,
        StaffIdentityProvisioningSourceKind sourceKind,
        Guid sourceId) =>
        DataRightsExportRecordIds.CreateDeterministicChild(
            staffMemberId,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)sourceKind}:{sourceId:N}"));

    public static Guid CreateResolution(
        Guid staffMemberId,
        StaffIdentityProvisioningSourceKind sourceKind,
        Guid sourceId) =>
        DataRightsExportRecordIds.CreateDeterministicChild(
            staffMemberId,
            string.Create(
                CultureInfo.InvariantCulture,
                $"resolution:{(int)sourceKind}:{sourceId:N}"));
}
