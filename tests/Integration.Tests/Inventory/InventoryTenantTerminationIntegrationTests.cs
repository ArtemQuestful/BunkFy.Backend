namespace Integration.Tests;

using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class InventoryTenantTerminationIntegrationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid AllocationId =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid ManagementOperationId =
        Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset ExportNowUtc =
        new(2026, 7, 31, 12, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        ExportNowUtc.AddMinutes(-1);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_repeatable_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_inventory_tenant_export_tests")
                .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        Guid receiptId;
        Guid restoreReceiptId;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            InventoryDbContext inventory = seedScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await inventory.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            (receiptId, restoreReceiptId) = await SeedGraphAsync(
                seedScope.ServiceProvider);
        }

        Guid otherTenantUnitId = Guid.NewGuid();
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            InventoryDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await SeedForeignUnitAsync(tenantBContext, otherTenantUnitId);
        }

        using IServiceScope scope = tenantAProvider.CreateScope();
        WorkspacesDbContext workspacesDbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence();
        workspacesDbContext.WorkspaceTerminationFences.Add(fence);
        await workspacesDbContext.SaveChangesAsync();

        ITenantTerminationExportContributor contributor =
            scope.ServiceProvider
                .GetServices<ITenantTerminationExportContributor>()
                .Single(candidate =>
                    candidate.ExportDescriptor.ExportSchemaId ==
                    InventoryTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("inventory.termination.exported", result.ResultCode);
        Assert.Equal(13, result.AffectedCount);
        Assert.Equal(
            InventoryTenantTerminationMetadata.RecordTypes,
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == otherTenantUnitId);
        Assert.Equal(
            "user:owner",
            Field(
                    first.Records.Single(record => record.RecordType ==
                        InventoryTenantTerminationMetadata.AllocationAnonymisationReceiptRecordType),
                    "inventory.staff-actor-reference")
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
            fence);
        await AssertAnonymisationReceiptsAreAppendOnlyAsync(
            scope.ServiceProvider,
            receiptId,
            restoreReceiptId);
    }

    private static async Task<(Guid ReceiptId, Guid RestoreReceiptId)>
        SeedGraphAsync(IServiceProvider services)
    {
        InventoryDbContext context = services
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(PropertyId, TenantA);
        property.Apply("Termination fixture", "termination-fixture", "UTC", PropertyStatus.Active, 1);
        context.PropertyTopology.Add(property);
        await context.SaveChangesAsync();
        IInventoryAllocationAmendmentDecisionRepository decisions = services
            .GetRequiredService<
                IInventoryAllocationAmendmentDecisionRepository>();
        IInventoryManagementOperationRepository managementOperations =
            services.GetRequiredService<
                IInventoryManagementOperationRepository>();
        InventoryUnit unit = CreateUnit(TenantA, BedId);
        RoomInventoryConfiguration configuration =
            RoomInventoryConfiguration.Create(
                RoomId,
                TenantA,
                PropertyId,
                FrozenAtUtc.AddDays(-5)).Value;
        Assert.True(configuration.Configure(
            RoomSalesMode.BedLevel,
            configuration.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-4),
            "user:owner").IsSuccess);
        ManualInventoryBlock block = ManualInventoryBlock.Create(
            Guid.Parse("41000000-0000-0000-0000-000000000001"),
            Guid.Parse("42000000-0000-0000-0000-000000000001"),
            TenantA,
            PropertyId,
            BedId,
            DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
            DateOnly.FromDateTime(FrozenAtUtc.AddDays(1).UtcDateTime),
            "maintenance",
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-1),
            "user:owner").Value;
        string membershipPayload =
            "bunkfy-inventory-manual-block-group-membership/v1" +
            "|kind=5|building=0:|floor=0:|room=|unit=" + BedId.ToString("N") +
            "|arrival=" + block.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "|departure=" + block.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "|members=1|" + BedId.ToString("N");
        string membershipDigest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(membershipPayload)));
        ManualInventoryBlockGroup group = ManualInventoryBlockGroup.Create(
            block.BlockGroupId, TenantA, PropertyId,
            ManualInventoryBlockGroupTargetKind.Unit,
            buildingLabel: null, floorLabel: null, roomId: null, inventoryUnitId: BedId,
            block.Arrival, block.Departure, block.Reason, Digest, membershipDigest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount: 1, replacesGroupId: null, block.CreatedAtUtc, "user:owner").Value;
        Guid reservationId =
            Guid.Parse("43000000-0000-0000-0000-000000000001");
        InventoryAllocation allocation =
            InventoryAllocation.CreateRejected(
                AllocationId,
                TenantA,
                reservationId,
                Guid.Parse("44000000-0000-0000-0000-000000000001"),
                PropertyId,
                DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
                DateOnly.FromDateTime(FrozenAtUtc.AddDays(1).UtcDateTime),
                [BedId],
                InventoryAllocationRejection.AllocationConflict,
                FrozenAtUtc.AddDays(-2)).Value;
        InventoryAllocationAnonymisationOutcome outcome =
            allocation.Anonymise(
                allocation.Version,
                Guid.Parse("45000000-0000-0000-0000-000000000001"),
                FrozenAtUtc.AddHours(-1)).Value;
        InventoryAllocationAnonymisationReceipt receipt =
            InventoryAllocationAnonymisationReceipt.Create(
                Guid.Parse("46000000-0000-0000-0000-000000000001"),
                TenantA,
                Guid.Parse("47000000-0000-0000-0000-000000000001"),
                Guid.Parse("48000000-0000-0000-0000-000000000001"),
                PropertyId,
                Guid.Parse("49000000-0000-0000-0000-000000000001"),
                approvalRevision: 1,
                operationRevision: 2,
                AllocationId,
                outcome,
                removedAmendmentDecisionCount: 1,
                Digest,
                "user:owner").Value;
        InventoryAllocationAnonymisationTombstone tombstone =
            InventoryAllocationAnonymisationTombstone.Create(receipt).Value;
        Guid ledgerEntryId =
            Guid.Parse("4a000000-0000-0000-0000-000000000001");
        DateTimeOffset replayedAtUtc = FrozenAtUtc.AddMinutes(-30);
        Assert.True(tombstone.AttachRestoreProof(
            PropertyId,
            receipt.ContractVersion,
            receipt.Id,
            receipt.CanonicalSha256,
            receipt.ResultingAllocationVersion,
            receipt.ResultingReservationPseudonym,
            allocationPresent: true,
            receipt.CompletedAtUtc,
            ledgerEntryId,
            replayedAtUtc).IsSuccess);
        InventoryAllocationAnonymisationRestoreReceipt restoreReceipt =
            InventoryAllocationAnonymisationRestoreReceipt.Create(
                TenantA,
                ledgerEntryId,
                tenantSequence: 1,
                Digest,
                PropertyId,
                AllocationId,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingAllocationVersion,
                receipt.ResultingReservationPseudonym,
                allocationPresent: true,
                receipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;
        BedRetirementProcess bedRetirement = BedRetirementProcess.Create(
            Guid.Parse("4b000000-0000-0000-0000-000000000001"),
            TenantA,
            PropertyId,
            RoomId,
            BedId,
            "replace frame",
            "user:owner",
            FrozenAtUtc.AddDays(-1)).Value;
        RoomRetirementProcess roomRetirement =
            RoomRetirementProcess.Create(
                Guid.Parse("4c000000-0000-0000-0000-000000000001"),
                TenantA,
                PropertyId,
                RoomId,
                "renovation",
                "user:owner",
                FrozenAtUtc.AddDays(-1)).Value;

        context.InventoryUnits.Add(unit);
        context.RoomConfigurations.Add(configuration);
        await managementOperations.AddAsync(
            new InventoryManagementOperationRecord(
                ManagementOperationId,
                TenantA,
                PropertyId,
                InventoryManagementResourceKind.Room,
                RoomId,
                InventoryManagementMutationKind
                    .RoomSalesModeConfiguration,
                ExpectedVersion: 1,
                Digest,
                InventorySalesMode.BedLevel,
                null,
                null,
                null,
                null,
                ResultVersion: 2,
                FrozenAtUtc.AddDays(-4)),
            CancellationToken.None);
        context.ManualBlockGroups.Add(group);
        context.ManualBlocks.Add(block);
        context.Allocations.Add(allocation);
        context.AllocationAnonymisationReceipts.Add(receipt);
        context.AllocationAnonymisationTombstones.Add(tombstone);
        context.AllocationAnonymisationRestoreReceipts.Add(restoreReceipt);
        context.BedRetirements.Add(bedRetirement);
        context.RoomRetirements.Add(roomRetirement);
        await decisions.AddAsync(
            new InventoryAllocationAmendmentDecisionRecord(
                Guid.Parse("4d000000-0000-0000-0000-000000000001"),
                TenantA,
                AllocationId,
                reservationId,
                PropertyId,
                Digest,
                Confirmed: false,
                InventoryAllocationRejectionReason.AllocationConflict,
                AllocationVersion: null,
                FrozenAtUtc.AddDays(-1)),
            CancellationToken.None);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return (receipt.Id, restoreReceipt.Id);
    }

    private static async Task AssertExportSerializesOperationalMutationAsync(
        ITenantTerminationExportContributor contributor,
        IServiceProvider rootServices,
        WorkspaceTerminationFence fence)
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

        Task write = AttemptOperationalWriteAsync(rootServices);
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
            "The workspace is not accepting Inventory mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices)
    {
        using IServiceScope scope = rootServices.CreateScope();
        InventoryDbContext context = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryUnit unit = await context.InventoryUnits.SingleAsync(
            candidate => candidate.Id == BedId);
        unit.TouchAvailability();
        await context.SaveChangesAsync();
    }

    private static async Task AssertAnonymisationReceiptsAreAppendOnlyAsync(
        IServiceProvider services,
        Guid receiptId,
        Guid restoreReceiptId)
    {
        InventoryDbContext context = services
            .GetRequiredService<InventoryDbContext>();
        const string tamperedActor = "tampered";
        PostgresException update = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.allocation_anonymisation_receipts
                SET "ActorId" = {tamperedActor}
                WHERE "Id" = {receiptId};
                """));
        Assert.Equal("P0001", update.SqlState);
        Assert.Contains(
            "inventory anonymisation receipts are append-only",
            update.MessageText,
            StringComparison.Ordinal);

        PostgresException delete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM inventory.allocation_anonymisation_restore_receipts
                WHERE "Id" = {restoreReceiptId};
                """));
        Assert.Equal("P0001", delete.SqlState);
    }

    private static async Task SeedForeignUnitAsync(InventoryDbContext context, Guid bedId)
    {
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(ForeignPropertyId, TenantB);
        property.Apply("Foreign fixture", "foreign-fixture", "UTC", PropertyStatus.Active, 1);
        context.PropertyTopology.Add(property);
        context.InventoryUnits.Add(CreateUnit(TenantB, bedId));
        await context.SaveChangesAsync();
    }

    private static readonly Guid ForeignPropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid ForeignRoomId =
        Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static InventoryUnit CreateUnit(
        string tenantId,
        Guid bedId)
    {
        Guid propertyId = tenantId == TenantB ? ForeignPropertyId : PropertyId;
        Guid roomId = tenantId == TenantB ? ForeignRoomId : RoomId;
        InventoryUnit unit = InventoryUnit.CreateBed(
            bedId,
            tenantId,
            propertyId,
            roomId);
        unit.Apply(
            propertyId,
            roomId,
            bedId,
            InventoryUnitKind.Bed,
            "1",
            isTopologyActive: true,
            sourceVersion: 2);
        return unit;
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

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId,
        TestClock? clock = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.Services.AddSingleton<ISystemClock>(
            clock ?? new TestClock(ExportNowUtc));
        builder.AddWorkspacesPersistence();
        builder.AddInventoryPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
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
        private readonly TaskCompletionSource firstRecord = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int recordCount;

        public Task FirstRecordObserved => this.firstRecord.Task;

        public async ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (Interlocked.Increment(ref this.recordCount) == 1)
            {
                this.firstRecord.TrySetResult();
                await this.release.Task.WaitAsync(cancellationToken);
            }
        }

        public void Release() => this.release.TrySetResult();
    }
}
