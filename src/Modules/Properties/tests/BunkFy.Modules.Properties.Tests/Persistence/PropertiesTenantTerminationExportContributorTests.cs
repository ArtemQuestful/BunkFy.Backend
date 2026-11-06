namespace BunkFy.Modules.Properties.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using DomainPropertyGovernanceAcknowledgement =
    BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

[Trait("Category", "Unit")]
public sealed partial class PropertiesTenantTerminationExportContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ProcessId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyMutationOperationId =
        Guid.Parse("51000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomMutationOperationId =
        Guid.Parse("52000000-0000-0000-0000-000000000001");
    private static readonly Guid BedMutationOperationId =
        Guid.Parse("53000000-0000-0000-0000-000000000001");
    private static readonly Guid BedBatchMutationOperationId =
        Guid.Parse("54000000-0000-0000-0000-000000000001");
    private static readonly Guid TimeZoneOperationId =
        Guid.Parse("55000000-0000-0000-0000-000000000001");
    private static readonly Guid TimeZoneRevisionId =
        Guid.Parse("56000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId =
        Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondBedId =
        Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdBedId =
        Guid.Parse("70000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset FrozenAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        FrozenAtUtc.AddMinutes(1);

    [Fact]
    public void Descriptor_declares_phase_specific_dependencies()
    {
        MutableFenceReader fences = new();
        using PropertiesDbContext context = CreateContext(fences);
        PropertiesTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);

        Assert.Collection(
            contributor.Descriptor.PhasePlans,
            export =>
            {
                Assert.Equal(
                    TenantTerminationContributionPhase.Export,
                    export.Phase);
                Assert.Equal(
                    [PropertiesTenantTerminationMetadata.ExportDependencyOwnerKey],
                    export.DependsOnOwnerKeys);
            },
            destroy =>
            {
                Assert.Equal(
                    TenantTerminationContributionPhase.Destroy,
                    destroy.Phase);
                Assert.Equal(
                    PropertiesTenantTerminationMetadata
                        .DestroyDependencyOwnerKeys,
                    destroy.DependsOnOwnerKeys);
            });
    }

    [Fact]
    public async Task Export_streams_complete_topology_and_governance_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        PropertiesTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("properties.termination.exported", result.ResultCode);
        Assert.Equal(12, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            [
                PropertiesTenantTerminationMetadata.PropertyRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyMutationOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyMutationOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyMutationOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyMutationOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyTimeZoneOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .GovernanceAcknowledgementRecordType,
                PropertiesTenantTerminationMetadata.RoomRecordType,
                PropertiesTenantTerminationMetadata.BedRecordType,
                PropertiesTenantTerminationMetadata.BedRecordType,
                PropertiesTenantTerminationMetadata.BedRecordType,
                PropertiesTenantTerminationMetadata
                    .GovernanceRevisionRecordType
            ],
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.Equal(
            "user:owner",
            Field(
                    first.Records[^1],
                    "properties.staff-actor-reference")
                .GetString());
        Assert.Equal(
            "processing-activation",
            Field(
                    first.Records[1],
                    "properties.property-mutation-operation")
                .GetProperty("kind")
                .GetString());
        JsonElement roomOperation = Field(
            first.Records[2],
            "properties.property-mutation-operation");
        Assert.Equal(
            "room",
            roomOperation.GetProperty("resourceKind").GetString());
        Assert.Equal(
            RoomId,
            roomOperation.GetProperty("resourceId").GetGuid());
        Assert.Equal(
            "room-update",
            roomOperation.GetProperty("kind").GetString());
        JsonElement bedOperation = Field(
            first.Records[3],
            "properties.property-mutation-operation");
        Assert.Equal(
            "bed-add",
            bedOperation.GetProperty("kind").GetString());
        Assert.Equal(
            BedId,
            bedOperation.GetProperty("resultBedId").GetGuid());
        Assert.Equal(
            "active",
            bedOperation.GetProperty("resultBedStatus").GetString());
        JsonElement batchOperation = Field(
            first.Records[4],
            "properties.property-mutation-operation");
        Assert.Equal(
            "bed-batch-add",
            batchOperation.GetProperty("kind").GetString());
        Assert.Equal(
            2,
            batchOperation.GetProperty("resultAffectedBedCount").GetInt32());
        JsonElement timeZoneOperation = Field(
            first.Records[5],
            "properties.property-time-zone-operation");
        Assert.Equal(
            "unchanged",
            timeZoneOperation.GetProperty("changeKind").GetString());
        Assert.Equal(
            "user:owner",
            Field(
                    first.Records[5],
                    "properties.staff-actor-reference")
                .GetString());
        Assert.Equal(
            PropertiesTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(8, contributor.ExportDescriptor.CatalogVersion);
        Assert.Equal(7, contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.Equal(5,
            PropertiesTenantTerminationMetadata.PersonalDataCatalogVersion);
        Assert.Contains(
            PropertiesTenantTerminationMetadata.PropertyTimeZoneOperationRecordType,
            PropertiesTenantTerminationMetadata.RecordTypes);
        Assert.Contains(
            "properties.property-time-zone-operation",
            contributor.ExportDescriptor.FieldIds);
        Assert.DoesNotContain(
            "catalog=7",
            PropertiesTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "export-schema=properties.tenant-termination-export:6",
            PropertiesTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.Equal(
            PropertiesTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Export_retries_without_records_for_a_different_fence()
    {
        MutableFenceReader fences = new()
        {
            Current = FrozenFence() with { Version = 2 }
        };
        await using PropertiesDbContext context = CreateContext(fences);
        PropertiesTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "properties.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Operational_save_rejects_a_frozen_workspace_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        context.Properties.Add(CreateProperty(PropertyId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        context.Properties.Add(CreateProperty(Guid.NewGuid(), "SECOND"));

        PropertiesOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                PropertiesOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            PropertiesOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.Single(await context.Properties.ToListAsync());
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        Property property = CreateProperty(PropertyId);
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        Assert.True(property.Update(
            "Updated hostel",
            property.Code.Value,
            property.TimeZoneId.Value,
            property.Version,
            Guid.NewGuid(),
            Now).IsSuccess);
        await context.SaveChangesAsync();

        Assert.Equal(
            2,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Operational_save_fails_closed_when_fence_read_fails()
    {
        await using PropertiesDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.Properties.Add(CreateProperty(PropertyId));

        PropertiesOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                PropertiesOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            PropertiesOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Properties.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Governance_revisions_are_append_only(
        EntityState attemptedState)
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PropertyGovernanceRevision revision =
            await context.GovernanceRevisions.SingleAsync();
        context.Entry(revision).State = attemptedState;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal(
            "Property governance revisions are append-only.",
            failure.Message);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Property_mutation_operations_are_append_only(
        EntityState attemptedState)
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PropertyMutationOperation operation =
            await context.PropertyMutationOperations.SingleAsync(
                item => item.Id == PropertyMutationOperationId);
        context.Entry(operation).State = attemptedState;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal(
            "Property mutation operations are append-only.",
            failure.Message);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Property_time_zone_operations_are_append_only(
        EntityState attemptedState)
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PropertyTimeZoneOperation operation =
            await context.PropertyTimeZoneOperations.SingleAsync();
        context.Entry(operation).State = attemptedState;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal(
            "Property time-zone operations are append-only.",
            failure.Message);
    }

    private static void SeedGraph(PropertiesDbContext context)
    {
        Property property = CreateProperty(PropertyId);
        PropertyGovernanceBinding binding =
            PropertyGovernanceBinding.Create(
                "GB",
                "uk-hostel-policy",
                policyVersion: 3,
                "eu-west",
                "standard-transfer",
                "hostel-retention",
                retentionPolicyVersion: 2,
                Digest,
                FrozenAtUtc.AddDays(-10),
                FrozenAtUtc.AddDays(10),
                FrozenAtUtc.AddDays(-2)).Value;
        DomainPropertyGovernanceAcknowledgement acknowledgement =
            DomainPropertyGovernanceAcknowledgement.Create(
                "controller-terms",
                acknowledgementVersion: 2).Value;
        Assert.True(property.ActivateProcessing(
            binding,
            [acknowledgement],
            property.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-2),
            "user:owner").IsSuccess);

        Room room = Room.Create(
            RoomId,
            TenantId,
            PropertyId,
            "4A",
            "Main House",
            "4",
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).Value;
        Assert.True(room.Update(
            "4A",
            "Main House",
            "5",
            room.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).IsSuccess);
        Assert.True(room.AddBed(
            BedId,
            "1",
            room.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).IsSuccess);
        Assert.True(room.AddBeds(
            [
                new BedAdditionDefinition(
                    SecondBedId,
                    "2",
                    Guid.NewGuid()),
                new BedAdditionDefinition(
                    ThirdBedId,
                    "3",
                    Guid.NewGuid())
            ],
            room.Version,
            FrozenAtUtc.AddDays(-5)).IsSuccess);

        PropertyGovernanceRevisionCoordinates current = new(
            binding.OperatingCountryCode,
            binding.PolicyId,
            binding.PolicyVersion,
            binding.DataRegionId,
            binding.TransferProfileId,
            binding.RetentionPolicyId,
            binding.RetentionPolicyVersion,
            binding.ContentSha256,
            Digest);
        PropertyGovernanceRevision revision = new(new(
            Guid.Parse("80000000-0000-0000-0000-000000000001"),
            TenantId,
            PropertyId,
            property.Version,
            PropertyGovernanceRevisionAction.Activated,
            "policy-allowed",
            Previous: null,
            Current: current,
            "user:owner",
            FrozenAtUtc.AddDays(-2)));

        context.Properties.Add(property);
        context.PropertyMutationOperations.Add(
            new PropertyMutationOperation(
                PropertyMutationOperationRecord.ForProperty(
                PropertyMutationOperationId,
                TenantId,
                PropertyId,
                PropertyMutationKind.ProcessingActivation,
                expectedVersion: 1,
                Digest,
                new PropertyMutationReceiptDto(
                    PropertyId,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Enabled,
                    Version: 2),
                FrozenAtUtc.AddDays(-3))));
        context.PropertyMutationOperations.Add(
            new PropertyMutationOperation(
                PropertyMutationOperationRecord.ForRoom(
                    RoomMutationOperationId,
                    TenantId,
                    PropertyId,
                    PropertyMutationResourceKind.Room,
                    RoomId,
                    PropertyMutationKind.RoomUpdate,
                    expectedVersion: 1,
                    Digest,
                    new RoomMutationReceiptDto(
                        PropertyId,
                        RoomId,
                        RoomStatus.Active,
                        Version: 2),
                    resultResourceVersion: 2,
                    FrozenAtUtc.AddDays(-3))));
        context.PropertyMutationOperations.Add(
            new PropertyMutationOperation(
                PropertyMutationOperationRecord.ForBed(
                    BedMutationOperationId,
                    TenantId,
                    PropertyId,
                    RoomId,
                    PropertyMutationKind.BedAdd,
                    expectedVersion: 2,
                    Digest,
                    new BedMutationReceiptDto(
                        PropertyId,
                        RoomId,
                        BedId,
                        BedStatus.Active,
                        Version: 1,
                        RoomVersion: 3),
                    FrozenAtUtc.AddDays(-3))));
        context.PropertyMutationOperations.Add(
            new PropertyMutationOperation(
                PropertyMutationOperationRecord.ForBedBatch(
                    BedBatchMutationOperationId,
                    TenantId,
                    PropertyId,
                    RoomId,
                    PropertyMutationKind.BedBatchAdd,
                    expectedVersion: 3,
                    Digest,
                    new BedBatchMutationReceiptDto(
                        PropertyId,
                        RoomId,
                        AffectedBedCount: 2,
                        RoomVersion: 5),
                    FrozenAtUtc.AddDays(-3))));
        context.PropertyTimeZoneOperations.Add(
            new PropertyTimeZoneOperation(
                new PropertyTimeZoneRevisionWriteModel(
                    TimeZoneRevisionId,
                    TenantId,
                    PropertyId,
                    TimeZoneOperationId,
                    PropertyTimeZoneChangeKind.Unchanged,
                    "Europe/London",
                    "Europe/London",
                    "Europe/London",
                    "TZDB: 2026c",
                    property.Version,
                    property.Version,
                    "user:owner",
                    FrozenAtUtc.AddDays(-2))));
        context.Rooms.Add(room);
        context.PropertyOperationLocks.Add(
            new PropertyOperationLock(property.Id, property.ScopeId));
        context.RoomOperationLocks.Add(
            new RoomOperationLock(room.Id, room.ScopeId));
        context.GovernanceRevisions.Add(revision);
    }

    private static Property CreateProperty(
        Guid propertyId,
        string code = "LIKE") =>
        Property.Create(
            propertyId,
            TenantId,
            $"Hostel {code}",
            code,
            "Europe/London",
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).Value;

    private static WorkspaceTerminationFenceSnapshot FrozenFence() =>
        new(
            ProcessId,
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            Version: 3);

    private static TenantTerminationExportRequest Request() =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantId,
                ProcessId,
                CaseId,
                ApprovalRevision: 1,
                OperationRevision: 2,
                TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Guid.Parse("a0000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            WorkspaceFenceRevision: 3,
            Digest,
            FrozenAtUtc);

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static PropertiesDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences) =>
        CreateContext(
            fences,
            TenantId,
            Guid.NewGuid().ToString("N"),
            new InMemoryDatabaseRoot());

    private static PropertiesDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences,
        string tenantId,
        string databaseName,
        InMemoryDatabaseRoot databaseRoot)
    {
        DbContextOptions<PropertiesDbContext> options =
            new DbContextOptionsBuilder<PropertiesDbContext>()
                .UseInMemoryDatabase(databaseName, databaseRoot)
                .Options;
        return new(options, new TestScopeContext(tenantId), fences);
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

    private sealed class MutableFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public WorkspaceTerminationFenceSnapshot? Current { get; set; }

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Current);
        }
    }

    private sealed class ThrowingFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fence store unavailable.");
    }

    private sealed class TestScopeContext(string scopeId = TenantId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
