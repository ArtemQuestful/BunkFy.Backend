namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed partial class StaffTenantTerminationContributor
{
    private async Task<long> ExportIdentityProvisioningAnchorsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (StaffIdentityProvisioningAnchor anchor in
            dbContext.IdentityProvisioningAnchors
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.StaffMemberId)
                .ThenBy(item => item.SourceKind)
                .ThenBy(item => item.SourceId)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            StaffIdentityProvisioningAnchorTenantExport record = new(
                anchor.ScopeId,
                anchor.StaffMemberId,
                new StaffIdentityProvisioningAnchorStateTenantExport(
                    anchor.SourceKind,
                    anchor.SourceId,
                    anchor.ResolutionEventId,
                    anchor.AnchoredAtUtc));
            await sink.WriteAsync(
                StaffTenantTerminationExportSchema.CreateRecord(
                    StaffTenantTerminationMetadata
                        .IdentityProvisioningAnchorRecordType,
                    IdentityProvisioningAnchorRecordId.Create(
                        anchor.StaffMemberId,
                        anchor.SourceKind,
                        anchor.SourceId),
                    recordVersion: 1,
                    record),
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportIdentityProvisioningAnchorResolutionsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (StaffIdentityProvisioningAnchorResolution resolution in
            dbContext.IdentityProvisioningAnchorResolutions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.StaffMemberId)
                .ThenBy(item => item.SourceKind)
                .ThenBy(item => item.SourceId)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            StaffIdentityProvisioningAnchorResolutionTenantExport record = new(
                resolution.ScopeId,
                resolution.StaffMemberId,
                new StaffIdentityProvisioningAnchorResolutionStateTenantExport(
                    resolution.SourceKind,
                    resolution.SourceId,
                    resolution.WorkspaceApplicationVersion,
                    resolution.Disposition,
                    resolution.ResolutionEventId,
                    resolution.ResolvedAtUtc));
            await sink.WriteAsync(
                StaffTenantTerminationExportSchema.CreateRecord(
                    StaffTenantTerminationMetadata
                        .IdentityProvisioningAnchorResolutionRecordType,
                    IdentityProvisioningAnchorRecordId.CreateResolution(
                        resolution.StaffMemberId,
                        resolution.SourceKind,
                        resolution.SourceId),
                    resolution.WorkspaceApplicationVersion,
                    record),
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }
}
