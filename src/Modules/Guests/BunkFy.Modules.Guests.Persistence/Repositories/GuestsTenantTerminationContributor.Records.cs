namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed partial class GuestsTenantTerminationContributor
{
    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        count = await this.ExportProfilesAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportManagementOperationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportGovernanceRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportRetentionRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportProfilesAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (GuestProfile profile in dbContext.GuestProfiles
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            GuestProfileTenantExport record = new(
                profile.ScopeId,
                profile.OriginPropertyId,
                profile.Id,
                new GuestProfileStateTenantExport(
                    profile.DisplayName,
                    profile.LegalName,
                    profile.Email,
                    profile.Phone,
                    profile.DateOfBirth,
                    profile.NationalityCountryCode,
                    profile.PreferredLanguageTag,
                    profile.Notes,
                    profile.CreationConfirmationId,
                    profile.Status,
                    profile.Version,
                    profile.CreatedAtUtc,
                    profile.LastChangedAtUtc,
                    profile.ArchivedAtUtc,
                    profile.AnonymisedAtUtc)
                {
                    LanguageTags = profile.LanguageTags
                },
                new GuestProfileStaffTenantExport(
                    profile.CreatedBy,
                    profile.LastChangedBy));
            await WriteAsync(
                GuestsTenantTerminationMetadata.GuestProfileRecordType,
                profile.Id,
                profile.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportManagementOperationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (GuestManagementOperation operation in
            dbContext.ManagementOperations
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.GuestId)
                .ThenBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            GuestManagementOperationTenantExport record = new(
                operation.ScopeId,
                operation.PropertyId,
                operation.GuestId,
                operation.Id,
                new GuestManagementOperationStateTenantExport(
                    operation.Kind,
                    operation.ExpectedVersion,
                    operation.RequestFingerprint,
                    operation.ResultStatus,
                    operation.ResultVersion,
                    operation.CompletedAtUtc));
            await WriteAsync(
                GuestsTenantTerminationMetadata.ManagementOperationRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    operation.GuestId,
                    operation.Id.ToString("N")),
                operation.ResultVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        sink.WriteAsync(
            GuestsTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);
}
