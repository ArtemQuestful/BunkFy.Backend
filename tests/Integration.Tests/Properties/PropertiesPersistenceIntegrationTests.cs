namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;
using System.Globalization;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Pagination;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainGovernanceAcknowledgement =
    BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

public sealed partial class PropertiesPersistenceIntegrationTests
{
    private const string InitialMigration = "20260709104355_InitialCreate";
    private const string TenantDestructionMigration =
        "20260804121920_AddPropertiesTenantDestructionLifecycle";
    private static readonly Guid PropertyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly DateTimeOffset ExportNowUtc =
        new(2026, 7, 31, 12, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        ExportNowUtc.AddMinutes(-1);
    private static readonly string Digest = new('a', 64);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Queries_and_tenant_export_use_authoritative_postgresql_records()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_properties_bed_listing_tests")
            .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        Property property;
        Room room;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            PropertiesDbContext dbContext = seedScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            await dbContext.Database.MigrateAsync();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await workspaces.Database.MigrateAsync();
            (property, room) = await SeedExportGraphAsync(
                seedScope.ServiceProvider);
        }

        Property otherTenantProperty;
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            PropertiesDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            IPropertyRepository tenantBProperties = tenantBScope
                .ServiceProvider
                .GetRequiredService<IPropertyRepository>();
            otherTenantProperty = CreateProperty(
                "other-hostel",
                "Other Hostel",
                TenantB);
            await tenantBProperties.AddAsync(
                otherTenantProperty,
                CancellationToken.None);
            await AppendCreatedTimeZoneOperationAsync(
                tenantBScope.ServiceProvider,
                otherTenantProperty,
                "user:other-owner");
            await tenantBContext.SaveChangesAsync();
        }

        using IServiceScope scope = tenantAProvider.CreateScope();
        WorkspacesDbContext workspacesDbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence();
        workspacesDbContext.WorkspaceTerminationFences.Add(fence);
        await workspacesDbContext.SaveChangesAsync();
        IPropertiesReadRepository repository = scope.ServiceProvider
            .GetRequiredService<IPropertiesReadRepository>();

        BedListResponse response = await repository.ListBedsAsync(
            property.Id,
            room.Id,
            new PageRequest(1, 20),
            CancellationToken.None);

        Assert.Equal(["A", "B"], response.Beds.Select(bed => bed.Label));
        Assert.All(response.Beds, bed => Assert.Equal(room.Version, bed.RoomVersion));

        ITenantTerminationExportContributor contributor = scope.ServiceProvider
            .GetServices<ITenantTerminationExportContributor>()
            .Single(candidate => candidate.ExportDescriptor.ExportSchemaId ==
                PropertiesTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("properties.termination.exported", result.ResultCode);
        Assert.Equal(7, result.AffectedCount);
        Assert.Equal(2, result.SelectedProofRevision);
        Assert.Equal(2, result.ResultingProofRevision);
        Assert.Equal(
            [
                PropertiesTenantTerminationMetadata.PropertyRecordType,
                PropertiesTenantTerminationMetadata
                    .PropertyTimeZoneOperationRecordType,
                PropertiesTenantTerminationMetadata
                    .GovernanceAcknowledgementRecordType,
                PropertiesTenantTerminationMetadata.RoomRecordType,
                PropertiesTenantTerminationMetadata.BedRecordType,
                PropertiesTenantTerminationMetadata.BedRecordType,
                PropertiesTenantTerminationMetadata
                    .GovernanceRevisionRecordType
            ],
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == otherTenantProperty.Id);
        Assert.Equal(
            "user:owner",
            Field(
                    first.Records[^1],
                    "properties.staff-actor-reference")
                .GetString());

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                replay,
                CancellationToken.None);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(RecordIdentity).ToArray(),
            replay.Records.Select(RecordIdentity).ToArray());

        await AssertExportSerializesOperationalMutationAsync(
            contributor,
            tenantAProvider,
            fence,
            property.Id);
        await AssertGovernanceHistoryIsAppendOnlyAsync(
            scope.ServiceProvider,
            property.Id);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Scope_and_lifecycle_migration_preserves_topology_and_enforces_concurrency()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_properties_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        await using (PropertiesDbContext initial = CreateDbContext(postgreSql.GetConnectionString()))
        {
            IMigrator migrator = initial.GetService<IMigrator>();
            await migrator.MigrateAsync(InitialMigration);
            await SeedInitialSchemaAsync(initial);
            await migrator.MigrateAsync(TenantDestructionMigration);
            await SeedCompletedTenantDestructionProgressAsync(initial);
            await migrator.MigrateAsync();
        }

        Guid secondPropertyId;
        Guid thirdPropertyId;
        using (ServiceProvider seedProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString()))
        using (IServiceScope seedScope = seedProvider.CreateScope())
        {
            IServiceProvider seedServices = seedScope.ServiceProvider;
            await seedServices.GetRequiredService<WorkspacesDbContext>()
                .Database.MigrateAsync();
            IPropertyRepository properties = seedServices
                .GetRequiredService<IPropertyRepository>();
            Property secondProperty = CreateProperty(
                "aaa-property",
                "AAA Property");
            Property thirdProperty = CreateProperty(
                "zzz-property",
                "ZZZ Property");
            await properties.AddAsync(secondProperty, CancellationToken.None);
            await properties.AddAsync(thirdProperty, CancellationToken.None);
            await AppendCreatedTimeZoneOperationAsync(
                seedServices,
                secondProperty,
                "system:migration-test");
            await AppendCreatedTimeZoneOperationAsync(
                seedServices,
                thirdProperty,
                "system:migration-test");
            await seedServices.GetRequiredService<PropertiesDbContext>()
                .SaveChangesAsync();
            secondPropertyId = secondProperty.Id;
            thirdPropertyId = thirdProperty.Id;
        }

        await using (PropertiesDbContext verification = CreateDbContext(postgreSql.GetConnectionString()))
        {
            Property property = await verification.Properties.SingleAsync(item => item.Id == PropertyId);
            Room room = await verification.Rooms.Include(item => item.Beds).SingleAsync(item => item.Id == RoomId);
            Bed bed = Assert.Single(room.Beds);

            Assert.Equal("tenant-a", property.ScopeId);
            Assert.Equal("tenant-a", room.ScopeId);
            Assert.Equal("tenant-a", bed.ScopeId);
            Assert.Equal(1, property.Version);
            Assert.Equal(1, room.Version);
            Assert.Equal(1, bed.Version);
            Assert.True(property.ProjectionOrdinal > 0);
            Assert.Equal(
                1,
                await verification.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM properties.property_operation_locks
                    WHERE "PropertyId" = {PropertyId}
                        AND "ScopeId" = {TenantA}
                    """).SingleAsync());
            Assert.Equal(
                10,
                await verification.Database.SqlQuery<int>($"""
                    SELECT "Stage" AS "Value"
                    FROM properties.tenant_destroy_operations
                    WHERE "ScopeId" = {TenantA}
                    """).SingleAsync());
            Assert.Equal(
                1,
                await verification.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM properties.room_operation_locks
                    WHERE "RoomId" = {RoomId}
                        AND "ScopeId" = {TenantA}
                    """).SingleAsync());

            Property secondProperty = await verification.Properties
                .SingleAsync(item => item.Id == secondPropertyId);
            Property thirdProperty = await verification.Properties
                .SingleAsync(item => item.Id == thirdPropertyId);

            Property lowerOrdinal = secondProperty.ProjectionOrdinal < thirdProperty.ProjectionOrdinal
                ? secondProperty
                : thirdProperty;
            Property higherOrdinal = ReferenceEquals(lowerOrdinal, secondProperty) ? thirdProperty : secondProperty;
            Assert.True(lowerOrdinal.Update(
                lowerOrdinal.Name.Value,
                "zzz-cursor",
                lowerOrdinal.TimeZoneId.Value,
                lowerOrdinal.Version,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow).IsSuccess);
            Assert.True(higherOrdinal.Update(
                higherOrdinal.Name.Value,
                "aaa-cursor",
                higherOrdinal.TimeZoneId.Value,
                higherOrdinal.Version,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow).IsSuccess);
            await verification.SaveChangesAsync();
        }

        using (ServiceProvider provider = CreatePersistenceProvider(postgreSql.GetConnectionString()))
        using (IServiceScope scope = provider.CreateScope())
        {
            IPropertiesTopologyProjectionExportSource source =
                scope.ServiceProvider.GetRequiredService<IPropertiesTopologyProjectionExportSource>();
            ProjectionRebuildRequest request = new("inventory-topology", projectionVersion: 1, batchSize: 1);
            ProjectionReadBatch<PropertyTopologyProjectionExport> firstBatch =
                await source.ReadAsync(request, cursor: null, CancellationToken.None);
            ProjectionReadBatch<PropertyTopologyProjectionExport> secondBatch =
                await source.ReadAsync(request, firstBatch.NextCursor, CancellationToken.None);

            Assert.Equal(PropertyId, Assert.Single(firstBatch.Snapshots).PropertyId);
            Assert.Equal("zzz-cursor", Assert.Single(secondBatch.Snapshots).Code);
            Assert.True(
                long.TryParse(
                    firstBatch.NextCursor,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long ordinal) && ordinal > 0);
        }

        await using PropertiesDbContext first = CreateDbContext(postgreSql.GetConnectionString());
        await using PropertiesDbContext second = CreateDbContext(postgreSql.GetConnectionString());
        Room firstRoom = await first.Rooms.Include(item => item.Beds).SingleAsync(item => item.Id == RoomId);
        Room staleRoom = await second.Rooms.Include(item => item.Beds).SingleAsync(item => item.Id == RoomId);

        Assert.True(firstRoom.Update("101-A", null, null, firstRoom.Version, Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);
        await first.SaveChangesAsync();
        Assert.True(staleRoom.Update("101-B", null, null, staleRoom.Version, Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static async Task<(Property Property, Room Room)>
        SeedExportGraphAsync(IServiceProvider services)
    {
        PropertiesDbContext dbContext = services
            .GetRequiredService<PropertiesDbContext>();
        IPropertyGovernanceRevisionWriter revisionWriter = services
            .GetRequiredService<IPropertyGovernanceRevisionWriter>();
        IPropertyRepository properties = services
            .GetRequiredService<IPropertyRepository>();
        IRoomRepository rooms = services
            .GetRequiredService<IRoomRepository>();
        Property property = CreateProperty("hostel-one", "Hostel One");
        await properties.AddAsync(property, CancellationToken.None);
        await AppendCreatedTimeZoneOperationAsync(
            services,
            property,
            "user:owner");
        await dbContext.SaveChangesAsync();

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
        DomainGovernanceAcknowledgement acknowledgement =
            DomainGovernanceAcknowledgement.Create(
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
            Guid.NewGuid(),
            TenantA,
            property.Id,
            "101",
            null,
            null,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).Value;
        Assert.True(room.AddBed(
            Guid.NewGuid(),
            "B",
            room.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).IsSuccess);
        Assert.True(room.AddBed(
            Guid.NewGuid(),
            "A",
            room.Version,
            Guid.NewGuid(),
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
        await rooms.AddAsync(room, CancellationToken.None);
        await revisionWriter.AppendAsync(
            new PropertyGovernanceRevisionWriteModel(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                TenantA,
                property.Id,
                property.Version,
                PropertyGovernanceRevisionAction.Activated,
                "policy-allowed",
                Previous: null,
                Current: current,
                "user:owner",
                FrozenAtUtc.AddDays(-2)),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        return (property, room);
    }

    private static async Task AssertExportSerializesOperationalMutationAsync(
        ITenantTerminationExportContributor contributor,
        IServiceProvider rootServices,
        WorkspaceTerminationFence fence,
        Guid propertyId)
    {
        BlockingSink sink = new();
        Task<TenantTerminationContributionResult> export =
            contributor.ExportAsync(
                TenantTerminationRequest(fence),
                sink,
                CancellationToken.None);
        Assert.Same(
            sink.FirstRecordObserved,
            await Task.WhenAny(sink.FirstRecordObserved, export));

        Task write = AttemptOperationalWriteAsync(rootServices, propertyId);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            sink.Release();
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            (await export).Status);
        InvalidOperationException failure =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => write);
        Assert.Equal(
            "The workspace is not accepting Properties mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices,
        Guid propertyId)
    {
        using IServiceScope scope = rootServices.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        Property property = await dbContext.Properties.SingleAsync(candidate =>
            candidate.Id == propertyId);
        Assert.True(property.Update(
            "Blocked update",
            property.Code.Value,
            property.TimeZoneId.Value,
            property.Version,
            Guid.NewGuid(),
            ExportNowUtc).IsSuccess);
        await dbContext.SaveChangesAsync();
    }

    private static async Task AssertGovernanceHistoryIsAppendOnlyAsync(
        IServiceProvider services,
        Guid propertyId)
    {
        PropertiesDbContext dbContext = services
            .GetRequiredService<PropertiesDbContext>();
        const string tamperedReason = "tampered";
        PostgresException update = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.property_governance_revisions
                SET "DecisionReasonCode" = {tamperedReason}
                WHERE "PropertyId" = {propertyId};
                """));
        Assert.Equal("P0001", update.SqlState);
        Assert.Contains(
            "property governance revisions are append-only",
            update.MessageText,
            StringComparison.Ordinal);

        PostgresException delete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM properties.property_governance_revisions
                WHERE "PropertyId" = {propertyId};
                """));
        Assert.Equal("P0001", delete.SqlState);
    }

    private static WorkspaceTerminationFence CreateTerminationFence() =>
        WorkspaceTerminationFence.Freeze(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            TenantA,
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            approvalRevision: 1,
            Guid.Parse("80000000-0000-0000-0000-000000000001"),
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;

    private static TenantTerminationExportRequest TenantTerminationRequest(
        WorkspaceTerminationFence fence) =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantA,
                fence.ProcessId,
                fence.CaseId,
                fence.ApprovalRevision,
                OperationRevision: 2,
                fence.TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Guid.Parse("a0000000-0000-0000-0000-000000000001"),
                fence.PolicyEvidenceSha256,
                "termination-exporter",
                ExportNowUtc.AddMinutes(5)),
            FreezeOperationRevision: 1,
            fence.Version,
            Digest,
            FrozenAtUtc);

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string RecordIdentity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static async Task SeedInitialSchemaAsync(PropertiesDbContext dbContext)
    {
        DateTimeOffset createdAtUtc = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        const string tenantId = "tenant-a";
        const string propertyName = "Hostel One";
        const string propertyCode = "hostel-one";
        const string timeZoneId = "UTC";
        const string roomName = "101";
        const string bedLabel = "A";
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO properties.properties
                ("Id", "Name", "Code", "TimeZoneId", "Status", "CreatedAtUtc", "TenantId")
            VALUES
                ({PropertyId}, {propertyName}, {propertyCode}, {timeZoneId}, {1}, {createdAtUtc}, {tenantId});

            INSERT INTO properties.rooms
                ("Id", "PropertyId", "Name", "Status", "CreatedAtUtc", "TenantId")
            VALUES
                ({RoomId}, {PropertyId}, {roomName}, {1}, {createdAtUtc}, {tenantId});

            INSERT INTO properties.beds
                ("Id", "TenantId", "PropertyId", "RoomId", "Label", "Status", "CreatedAtUtc")
            VALUES
                ({BedId}, {tenantId}, {PropertyId}, {RoomId}, {bedLabel}, {1}, {createdAtUtc});
            """);
    }

    private static Task<int> SeedCompletedTenantDestructionProgressAsync(
        PropertiesDbContext dbContext)
    {
        Guid operationId =
            Guid.Parse("f0000000-0000-0000-0000-000000000001");
        DateTimeOffset startedAtUtc =
            new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO properties.tenant_destroy_operations
                ("OperationId", "ScopeId", "RequestSha256",
                 "SelectedRevision", "ResultingRevision", "BatchSize",
                 "Stage", "RemovedRecordCount", "CompletedBatchCount",
                 "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                 "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES
                ({operationId}, {TenantA}, {Digest},
                 {1L}, {2L}, {500}, {8}, {0L}, {0},
                 {1}, {Digest}, {startedAtUtc}, {startedAtUtc}, {1});
            """);
    }

    private static PropertiesDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<PropertiesDbContext> options = new DbContextOptionsBuilder<PropertiesDbContext>()
            .UseNpgsql(
                connectionString,
                postgreSql => postgreSql
                    .MigrationsAssembly(PropertiesMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(PropertiesMigrations.HistoryTable, PropertiesMigrations.Schema))
            .Options;

        return new PropertiesDbContext(
            options,
            new TestScopeContext(),
            new NoTerminationFenceReader());
    }

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId = TenantA,
        TestClock? clock = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.Services.AddSingleton<ISystemClock>(
            clock ?? new TestClock(ExportNowUtc));
        builder.AddWorkspacesPersistence();
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddPropertiesPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private static Property CreateProperty(
        string code,
        string name,
        string tenantId = TenantA) =>
        Property.Create(
            Guid.NewGuid(),
            tenantId,
            name,
            code,
            "Etc/UTC",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow).Value;

    private static Task AppendCreatedTimeZoneOperationAsync(
        IServiceProvider services,
        Property property,
        string actorId) =>
        services.GetRequiredService<IPropertyTimeZoneRevisionWriter>()
            .AppendAsync(
                new PropertyTimeZoneRevisionWriteModel(
                    Guid.NewGuid(),
                    property.ScopeId,
                    property.Id,
                    property.Id,
                    PropertyTimeZoneChangeKind.Created,
                    property.TimeZoneId.Value,
                    null,
                    property.TimeZoneId.Value,
                    BunkFy.TimeZones.TimeZoneCatalog.Default.CatalogVersion,
                    0,
                    property.Version,
                    actorId,
                    property.CreatedAtUtc),
                CancellationToken.None);

    private sealed class TestScopeContext(string scopeId = TenantA)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
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

    private sealed class BlockingSink : IDataRightsExportSink
    {
        private readonly TaskCompletionSource firstRecordObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blocked;

        public Task FirstRecordObserved => this.firstRecordObserved.Task;

        public async ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.blocked, 1) == 0)
            {
                this.firstRecordObserved.TrySetResult();
                await this.release.Task.WaitAsync(cancellationToken);
            }
        }

        public void Release() => this.release.TrySetResult();
    }

    private sealed class NoTerminationFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        }
    }
}
