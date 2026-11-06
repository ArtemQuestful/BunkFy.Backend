namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestDataRightsExportIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Guest_export_streams_only_authorized_property_history_on_postgresql()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_data_rights_export_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid targetPropertyId = Guid.NewGuid();
        Guid unrelatedPropertyId = Guid.NewGuid();
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            "tenant-a",
            unrelatedPropertyId,
            "Maya Chen",
            "Maya Q. Chen",
            "maya.chen@example.test",
            "+44 20 1234 5678",
            new DateOnly(1991, 5, 17),
            "GB",
            "en-GB",
            "Prefers a lower bunk.",
            "user:operator",
            Guid.NewGuid(),
            Now, languageTags: ["en-GB", "fr", "unknown-١"]).Value;
        GuestStayHistoryEntry targetStay = CreateStay(profile.Id, targetPropertyId);
        GuestStayHistoryEntry unrelatedStay = CreateStay(profile.Id, unrelatedPropertyId);

        using ServiceProvider provider = CreatePersistenceProvider(postgreSql.GetConnectionString());
        using IServiceScope scope = provider.CreateScope();
        GuestsDbContext dbContext = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        await dbContext.Database.MigrateAsync();
        dbContext.PropertyProjections.Add(new GuestPropertyProjection(
            "tenant-a",
            targetPropertyId,
            "Target Property",
            PropertyStatus.Active,
            1));
        dbContext.GuestProfiles.Add(profile);
        dbContext.StayHistory.AddRange(targetStay, unrelatedStay);
        await dbContext.SaveChangesAsync();

        IDataRightsSubjectExportContributor contributor = scope.ServiceProvider
            .GetServices<IDataRightsSubjectExportContributor>()
            .Single(candidate => candidate.OwnerKey == "guests");
        CollectingSink sink = new();
        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            new DataRightsSubjectExportRequest(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                targetPropertyId,
                new DataRightsSubjectCoordinate(
                    contributor.OwnerKey,
                    "guest-profile",
                    profile.Id,
                    profile.Version)),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        Assert.Equal(2, result.RecordCount);
        DataRightsExportRecord profileRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == "guest-profile");
        Assert.Equal(
            "maya.chen@example.test",
            Field(profileRecord, "guest.profile.email").GetString());
        Assert.Equal(["en-gb", "fr", "unknown-١"],
            Field(profileRecord, "guest.profile.language-tags").EnumerateArray()
                .Select(value => Assert.IsType<string>(value.GetString())).ToArray());

        DataRightsExportRecord stayRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == "guest-stay");
        Assert.Equal(targetStay.ReservationId, stayRecord.RecordId);
        Assert.Equal(
            targetPropertyId,
            Field(stayRecord, "guest.stay.property-id").GetGuid());
        Assert.DoesNotContain(
            sink.Records,
            record => record.RecordId == unrelatedStay.ReservationId);
    }

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

    private static ServiceProvider CreatePersistenceProvider(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext("tenant-a"));
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddGuestsPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private static JsonElement Field(DataRightsExportRecord record, string fieldId) =>
        Assert.Single(record.Fields, field => field.FieldId == fieldId).Value;

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

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
