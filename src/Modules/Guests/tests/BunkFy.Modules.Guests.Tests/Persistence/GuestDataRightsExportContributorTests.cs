namespace BunkFy.Modules.Guests.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestDataRightsExportContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_is_catalogue_versioned_and_contains_only_the_scoped_subject()
    {
        await using GuestsDbContext dbContext = CreateDbContext("tenant-a");
        Guid targetPropertyId = Guid.NewGuid();
        Guid unrelatedPropertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            "tenant-a",
            unrelatedPropertyId,
            "Maya Chen",
            "Maya Q. Chen",
            "maya.chen@example.test",
            "+44 20 1234 5678");
        long expectedVersion = profile.Version;
        Assert.True(profile.Update(
            profile.DisplayName,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            profile.Notes,
            expectedVersion,
            "user:operator",
            Guid.NewGuid(),
            Now.AddMinutes(1),
            [profile.PreferredLanguageTag!, "fr", "unknown-١"]).IsSuccess);
        GuestManagementOperationRecord managementOperation = new(
            Guid.NewGuid(),
            profile.ScopeId,
            targetPropertyId,
            profile.Id,
            GuestManagementOperationKind.Update,
            expectedVersion,
            new string('a', 64),
            GuestStatus.Active,
            profile.Version,
            profile.LastChangedAtUtc);
        long unrelatedExpectedVersion = profile.Version;
        Assert.True(profile.Update(
            profile.DisplayName,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            "Updated from another property.",
            unrelatedExpectedVersion,
            "user:operator",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        GuestManagementOperationRecord unrelatedManagementOperation = new(
            Guid.NewGuid(),
            profile.ScopeId,
            unrelatedPropertyId,
            profile.Id,
            GuestManagementOperationKind.Update,
            unrelatedExpectedVersion,
            new string('b', 64),
            GuestStatus.Active,
            profile.Version,
            profile.LastChangedAtUtc);
        dbContext.PropertyProjections.Add(new GuestPropertyProjection(
            "tenant-a",
            targetPropertyId,
            "Target Property",
            PropertyStatus.Active,
            1));
        dbContext.GuestProfiles.Add(profile);
        dbContext.ManagementOperations.AddRange(
            new GuestManagementOperation(managementOperation),
            new GuestManagementOperation(unrelatedManagementOperation));
        GuestStayHistoryEntry targetStay = CreateStay(profile.Id, targetPropertyId);
        GuestStayHistoryEntry unrelatedStay = CreateStay(profile.Id, unrelatedPropertyId);
        dbContext.StayHistory.AddRange(targetStay, unrelatedStay);
        await dbContext.SaveChangesAsync();

        GuestDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();
        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            Request("tenant-a", targetPropertyId, profile.Id, profile.Version),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RecordCount);
        Assert.Equal("guests.personal-data", contributor.Descriptor.CatalogId);
        Assert.Equal(1, contributor.Descriptor.CatalogSchemaVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.PersonalDataCatalogVersion,
            contributor.Descriptor.CatalogVersion);
        Assert.Equal(GuestDataRightsExportSchema.ExportSchemaId, contributor.Descriptor.ExportSchemaId);
        Assert.Equal(GuestDataRightsExportSchema.ExportSchemaVersion, contributor.Descriptor.ExportSchemaVersion);
        Assert.Equal(37, contributor.Descriptor.FieldIds.Count);
        Assert.Contains("guest.profile.language-tags", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain("guest.profile.audit-actor-id", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain("guest.profile.projection-ordinal", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain("guest.profile.tenant-scope-id", contributor.Descriptor.FieldIds);

        DataRightsExportRecord profileRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == GuestDataRightsDiscoveryContributor.ProfileRecordType);
        Assert.Equal(profile.Id, profileRecord.RecordId);
        Assert.Equal("Maya Chen", Field(profileRecord, "guest.profile.display-name").GetString());
        Assert.Equal(
            "maya.chen@example.test",
            Field(profileRecord, "guest.profile.email").GetString());
        Assert.Equal("active", Field(profileRecord, "guest.profile.status").GetString());
        Assert.Equal(profile.LanguageTags,
            Field(profileRecord, "guest.profile.language-tags").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(2, contributor.Descriptor.ExportSchemaVersion);
        Assert.Equal(17, contributor.Descriptor.CatalogVersion);

        DataRightsExportRecord stayRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == GuestDataRightsExportContributor.StayRecordType);
        Assert.Equal(targetStay.ReservationId, stayRecord.RecordId);
        Assert.Equal(
            targetPropertyId,
            Field(stayRecord, "guest.stay.property-id").GetGuid());
        Assert.DoesNotContain(
            sink.Records,
            record => record.RecordId == unrelatedStay.ReservationId);

        DataRightsExportRecord managementRecord = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                GuestDataRightsExportContributor.ManagementOperationRecordType);
        Assert.Equal(
            DataRightsExportRecordIds.CreateDeterministicChild(
                profile.Id,
                managementOperation.OperationId.ToString("N")),
            managementRecord.RecordId);
        Assert.Equal(
            managementOperation.RequestFingerprint,
            Field(
                managementRecord,
                "guest.management-operation.request-fingerprint").GetString());
        Assert.DoesNotContain(
            sink.Records,
            record => record.RecordType ==
                GuestDataRightsExportContributor.ManagementOperationRecordType &&
                Field(record, "guest.management-operation.id").GetGuid() ==
                    unrelatedManagementOperation.OperationId);
    }

    [Fact]
    public async Task Export_fails_closed_for_stale_or_unavailable_scope_without_writing()
    {
        await using GuestsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = Guid.NewGuid();
        Guid otherPropertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            "tenant-a",
            propertyId,
            "Guest",
            null,
            "guest@example.test",
            null);
        dbContext.PropertyProjections.AddRange(
            new GuestPropertyProjection(
                "tenant-a",
                propertyId,
                "Property",
                PropertyStatus.Active,
                1),
            new GuestPropertyProjection(
                "tenant-a",
                otherPropertyId,
                "Other Property",
                PropertyStatus.Active,
                1));
        dbContext.GuestProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        GuestDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink staleSink = new();
        CollectingSink wrongTenantSink = new();
        CollectingSink invisibleSink = new();

        DataRightsSubjectExportResult stale = await contributor.ExportAsync(
            Request("tenant-a", propertyId, profile.Id, profile.Version + 1),
            staleSink,
            CancellationToken.None);
        DataRightsSubjectExportResult wrongTenant = await contributor.ExportAsync(
            Request("tenant-b", propertyId, profile.Id, profile.Version),
            wrongTenantSink,
            CancellationToken.None);
        DataRightsSubjectExportResult invisible = await contributor.ExportAsync(
            Request("tenant-a", otherPropertyId, profile.Id, profile.Version),
            invisibleSink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Stale, stale.Status);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, wrongTenant.Status);
        Assert.Equal(DataRightsSubjectExportStatus.NotFound, invisible.Status);
        Assert.Empty(staleSink.Records);
        Assert.Empty(wrongTenantSink.Records);
        Assert.Empty(invisibleSink.Records);
    }

    [Fact]
    public async Task Staff_scope_is_not_exported_by_guests()
    {
        await using GuestsDbContext dbContext = CreateDbContext("tenant-a");
        GuestDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            new DataRightsSubjectExportRequest(
                "tenant-a",
                DataRightsCaseType.StaffRights,
                PropertyId: null,
                new DataRightsSubjectCoordinate(
                    GuestDataRightsDiscoveryContributor.Owner,
                    GuestDataRightsDiscoveryContributor.ProfileRecordType,
                    Guid.NewGuid(),
                    1)),
            sink,
            CancellationToken.None);

        Assert.Equal([DataRightsCaseType.GuestRights], contributor.SupportedCaseTypes);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Sink_failure_aborts_export_instead_of_returning_partial_success()
    {
        await using GuestsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            "tenant-a",
            propertyId,
            "Guest",
            null,
            null,
            null);
        dbContext.PropertyProjections.Add(new GuestPropertyProjection(
            "tenant-a",
            propertyId,
            "Property",
            PropertyStatus.Active,
            1));
        dbContext.GuestProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        GuestDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        RejectingSink sink = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => contributor.ExportAsync(
            Request("tenant-a", propertyId, profile.Id, profile.Version),
            sink,
            CancellationToken.None));
    }

    [Fact]
    public async Task Anonymised_guest_cannot_be_exported_from_the_ordinary_rights_surface()
    {
        await using GuestsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            "tenant-a",
            propertyId,
            "Guest",
            null,
            "guest@example.test",
            null);
        Assert.True(profile.Anonymise(
            profile.Version,
            "user:privacy",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        dbContext.PropertyProjections.Add(new GuestPropertyProjection(
            "tenant-a",
            propertyId,
            "Property",
            PropertyStatus.Active,
            1));
        dbContext.GuestProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        GuestDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            Request("tenant-a", propertyId, profile.Id, profile.Version),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.NotFound, result.Status);
        Assert.Empty(sink.Records);
    }

    private static DataRightsSubjectExportRequest Request(
        string tenantId,
        Guid propertyId,
        Guid guestId,
        long version) =>
        new(
            tenantId,
            DataRightsCaseType.GuestRights,
            propertyId,
            new(
                GuestDataRightsDiscoveryContributor.Owner,
                GuestDataRightsDiscoveryContributor.ProfileRecordType,
                guestId,
                version));

    private static GuestProfile CreateProfile(
        string tenantId,
        Guid originPropertyId,
        string displayName,
        string? legalName,
        string? email,
        string? phone) =>
        GuestProfile.Create(
            Guid.NewGuid(),
            tenantId,
            originPropertyId,
            displayName,
            legalName,
            email,
            phone,
            new DateOnly(1991, 5, 17),
            "GB",
            "en-GB",
            "Prefers a lower bunk.",
            "user:operator",
            Guid.NewGuid(),
            Now).Value;

    private static GuestStayHistoryEntry CreateStay(Guid guestId, Guid propertyId) =>
        new(
            "tenant-a",
            guestId,
            Guid.NewGuid(),
            propertyId,
            GuestStayRole.Primary,
            new DateOnly(2026, 7, 20),
            new DateOnly(2026, 7, 22),
            GuestStayStatus.CheckedOut,
            new DateOnly(2026, 7, 20),
            null,
            new DateOnly(2026, 7, 22),
            isCurrentParticipant: false,
            reservationVersion: 3,
            projectionContractVersion: GuestsModuleMetadata.StayHistoryProjectionVersion);

    private static JsonElement Field(DataRightsExportRecord record, string fieldId) =>
        Assert.Single(record.Fields, field => field.FieldId == fieldId).Value;

    private static GuestsDbContext CreateDbContext(string tenantId)
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase($"guests-data-rights-export-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RejectingSink : IDataRightsExportSink
    {
        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("Rejected."));
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
