namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class GuestDataRightsExportContributor(
    GuestsDbContext dbContext,
    IScopeContext scopeContext) : IDataRightsSubjectExportContributor
{
    public const string StayRecordType = "guest-stay";
    public const string ManagementOperationRecordType =
        "guest-management-operation";

    public string OwnerKey => GuestDataRightsDiscoveryContributor.Owner;

    public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes { get; } =
        [DataRightsCaseType.GuestRights];

    public DataRightsExportDescriptor Descriptor => GuestDataRightsExportSchema.Descriptor;

    public async Task<DataRightsSubjectExportResult> ExportAsync(
        DataRightsSubjectExportRequest request,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        if (!this.IsValidScope(request.CaseType, request.TenantId, request.PropertyId))
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        if (request.Coordinate is not { } coordinate ||
            !string.Equals(
                coordinate.OwnerKey,
                GuestDataRightsDiscoveryContributor.Owner,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                coordinate.RecordType,
                GuestDataRightsDiscoveryContributor.ProfileRecordType,
                StringComparison.OrdinalIgnoreCase) ||
            coordinate.RecordId == Guid.Empty ||
            coordinate.RecordVersion <= 0)
        {
            return DataRightsSubjectExportResult.NotFound();
        }

        Guid propertyId = request.PropertyId!.Value;
        if (!await this.IsKnownPropertyAsync(propertyId, cancellationToken).ConfigureAwait(false))
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        GuestProfileDataRightsExport? profile = await this.VisibleAtProperty(propertyId)
            .Where(candidate => candidate.Id == coordinate.RecordId)
            .Select(candidate => new GuestProfileDataRightsExport(
                candidate.Id,
                candidate.OriginPropertyId,
                candidate.DisplayName,
                candidate.LegalName,
                candidate.Email,
                candidate.Phone,
                candidate.DateOfBirth,
                candidate.NationalityCountryCode,
                candidate.PreferredLanguageTag,
                candidate.Notes,
                candidate.Status,
                candidate.Version,
                candidate.CreatedAtUtc,
                candidate.LastChangedAtUtc,
                candidate.ArchivedAtUtc)
            {
                LanguageTags = candidate.LanguageTags
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return DataRightsSubjectExportResult.NotFound();
        }

        if (profile.Version != coordinate.RecordVersion)
        {
            return DataRightsSubjectExportResult.Stale();
        }

        await sink.WriteAsync(
            GuestDataRightsExportSchema.CreateProfileRecord(profile),
            cancellationToken).ConfigureAwait(false);
        int recordCount = 1;

        IQueryable<GuestManagementOperationDataRightsExport> managementOperations =
            dbContext.ManagementOperations
                .AsNoTracking()
                .Where(operation =>
                    operation.GuestId == coordinate.RecordId &&
                    operation.PropertyId == propertyId)
                .OrderBy(operation => operation.CompletedAtUtc)
                .ThenBy(operation => operation.Id)
                .Select(operation => new GuestManagementOperationDataRightsExport(
                    operation.Id,
                    operation.ScopeId,
                    operation.PropertyId,
                    operation.GuestId,
                    operation.Kind,
                    operation.ExpectedVersion,
                    operation.RequestFingerprint,
                    operation.ResultStatus,
                    operation.ResultVersion,
                    operation.CompletedAtUtc));
        await foreach (GuestManagementOperationDataRightsExport operation in
                           managementOperations
                               .AsAsyncEnumerable()
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
        {
            await sink.WriteAsync(
                GuestDataRightsExportSchema.CreateManagementOperationRecord(operation),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        IQueryable<GuestStayDataRightsExport> stays = dbContext.StayHistory
            .AsNoTracking()
            .Where(stay =>
                stay.GuestId == coordinate.RecordId &&
                stay.PropertyId == propertyId)
            .OrderBy(stay => stay.ReservationId)
            .Select(stay => new GuestStayDataRightsExport(
                stay.ReservationId,
                stay.PropertyId,
                stay.Role,
                stay.Arrival,
                stay.Departure,
                stay.Status,
                stay.CheckedInBusinessDate,
                stay.NoShowBusinessDate,
                stay.CheckedOutBusinessDate,
                stay.IsCurrentParticipant,
                stay.ReservationVersion));
        await foreach (GuestStayDataRightsExport stay in stays
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            await sink.WriteAsync(
                GuestDataRightsExportSchema.CreateStayRecord(stay),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        return DataRightsSubjectExportResult.Success(recordCount);
    }

    private IQueryable<GuestProfile> VisibleAtProperty(Guid propertyId) =>
        dbContext.GuestProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.Status != GuestProfileState.Anonymised &&
                (profile.OriginPropertyId == propertyId ||
                 dbContext.StayHistory.Any(stay =>
                     stay.GuestId == profile.Id &&
                     stay.PropertyId == propertyId)));

    private Task<bool> IsKnownPropertyAsync(
        Guid propertyId,
        CancellationToken cancellationToken) =>
        dbContext.PropertyProjections
            .AsNoTracking()
            .AnyAsync(
                property => property.Id == propertyId && property.IsKnown,
                cancellationToken);

    private bool IsValidScope(
        DataRightsCaseType caseType,
        string tenantId,
        Guid? propertyId) =>
        caseType == DataRightsCaseType.GuestRights &&
        scopeContext.IsEnabled &&
        !string.IsNullOrWhiteSpace(scopeContext.ScopeId) &&
        string.Equals(scopeContext.ScopeId, tenantId?.Trim(), StringComparison.Ordinal) &&
        propertyId.HasValue &&
        propertyId.Value != Guid.Empty;
}
