namespace Integration.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.ProjectionRebuild;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class InventoryTenantTerminationIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_destroy_is_bounded_immutable_and_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_inventory_destroy_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        TestClock clock = new(ExportNowUtc);
        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            connectionString,
            TenantA,
            clock);
        using (IServiceScope migrationScope = tenantAProvider.CreateScope())
        {
            await migrationScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
            await migrationScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
        }

        await SeedGraphInScopeAsync(tenantAProvider).ConfigureAwait(false);
        await SeedDenseDestroyStateAsync(tenantAProvider, clock)
            .ConfigureAwait(false);
        await AssertProofTriggersProtectNormalTrafficAsync(tenantAProvider)
            .ConfigureAwait(false);

        Guid tenantBUnitId =
            Guid.Parse("b0000000-0000-0000-0000-000000000001");
        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB);
        using (IServiceScope tenantBSeedScope = tenantBProvider.CreateScope())
        {
            InventoryDbContext tenantB = tenantBSeedScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await SeedForeignUnitAsync(tenantB, tenantBUnitId).ConfigureAwait(false);
        }

        WorkspaceTerminationFence fence = CreateTerminationFence();
        using IServiceScope inFlightScope = tenantAProvider.CreateScope();
        InventoryDbContext inFlight = inFlightScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using IDbContextTransaction inFlightTransaction =
            await inFlight.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        InventoryUnit inFlightUnit = await inFlight.InventoryUnits
            .SingleAsync(unit => unit.Id == BedId)
            .ConfigureAwait(false);
        inFlightUnit.TouchAvailability();
        await inFlight.SaveChangesAsync().ConfigureAwait(false);

        using (IServiceScope fenceScope = tenantAProvider.CreateScope())
        {
            WorkspacesDbContext workspaces = fenceScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            workspaces.WorkspaceTerminationFences.Add(fence);
            Task<int> persistFence = workspaces.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
            Assert.False(persistFence.IsCompleted);
            await inFlightTransaction.CommitAsync().ConfigureAwait(false);
            await persistFence.ConfigureAwait(false);
        }

        long selectedRevision;
        using (IServiceScope revisionScope = tenantAProvider.CreateScope())
        {
            InventoryDbContext inventory = revisionScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            selectedRevision = await ReadTenantRevisionAsync(
                    inventory,
                    TenantA)
                .ConfigureAwait(false);
        }

        using IServiceScope ownerScope = tenantAProvider.CreateScope();
        ITenantTerminationContributor contributor = ownerScope.ServiceProvider
            .GetServices<ITenantTerminationContributor>()
            .Single(candidate => candidate.Descriptor.OwnerKey ==
                InventoryTenantTerminationMetadata.OwnerKey);
        TenantTerminationContributionRequest request =
            TenantDestroyRequest(fence);

        TenantTerminationContributionResult busy =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            busy.Status);
        Assert.Equal(
            "inventory.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(0, busy.AffectedCount);

        InventoryDbContext ownerContext = ownerScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        Assert.Equal(
            1,
            await ReadOperationCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            2,
            await ReadLifecycleStatusAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        using (IServiceScope claimScope = tenantAProvider.CreateScope())
        {
            IOutboxStore outbox = claimScope.ServiceProvider
                .GetServices<IOutboxStore>()
                .Single(store => store.ModuleName ==
                    InventoryModuleMetadata.Name);
            IReadOnlyList<OutboxMessageRecord> claims =
                await outbox.ClaimPendingAsync(
                    batchSize: 10,
                    "inventory-claim-test",
                    clock.UtcNow,
                    TimeSpan.FromMinutes(1),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.Empty(claims);
        }

        long[] expectedEarlyCounts = [1, 2, 3, 4, 5, 6];
        foreach (long expected in expectedEarlyCounts)
        {
            TenantTerminationContributionResult progress =
                await contributor.ExecuteAsync(
                    request,
                    CancellationToken.None)
                    .ConfigureAwait(false);
            Assert.Equal(
                TenantTerminationContributionStatus.RetryRequired,
                progress.Status);
            Assert.Equal(
                "inventory.termination.destroy-in-progress",
                progress.ResultCode);
            Assert.Equal(expected, progress.AffectedCount);
        }

        TenantTerminationContributionResult firstUnitBatch =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            firstUnitBatch.Status);
        Assert.Equal(506, firstUnitBatch.AffectedCount);
        Assert.Equal(
            1,
            await ReadAllocationUnitCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        TenantTerminationContributionResult finalUnitBatch =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            finalUnitBatch.Status);
        Assert.Equal(507, finalUnitBatch.AffectedCount);
        Assert.Equal(
            0,
            await ReadAllocationUnitCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        TenantTerminationContributionResult completed = finalUnitBatch;
        int attempts = 8;
        while (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 40)
        {
            completed = await contributor.ExecuteAsync(
                request,
                CancellationToken.None)
                .ConfigureAwait(false);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            completed.Status);
        Assert.Equal(
            "inventory.termination.destroyed",
            completed.ResultCode);
        Assert.Equal(525, completed.AffectedCount);
        Assert.Equal(selectedRevision, completed.SelectedProofRevision);
        Assert.Equal(
            selectedRevision + 1,
            completed.ResultingProofRevision);

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(completed, replay);
        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "inventory.termination.destroy-conflict",
            conflict.ResultCode);

        Assert.Equal(
            0,
            await ReadOwnerRecordCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await ReadOperationCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadReceiptCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            525,
            await ReadReceiptRemovedCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            3,
            await ReadLifecycleStatusAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        ownerContext.ChangeTracker.Clear();
        ownerContext.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.inventory.termination-test.v1",
            "termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            "{}",
            clock.UtcNow));
        InvalidOperationException closed =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => ownerContext.SaveChangesAsync())
                .ConfigureAwait(false);
        Assert.Equal(
            "The workspace is not accepting Inventory mutations.",
            closed.Message);
        ownerContext.ChangeTracker.Clear();

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ownerContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE inventory.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "inventory anonymisation receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        using (IServiceScope tenantBVerificationScope =
            tenantBProvider.CreateScope())
        {
            InventoryDbContext tenantB =
                tenantBVerificationScope.ServiceProvider
                    .GetRequiredService<InventoryDbContext>();
            Assert.Equal(
                2,
                await ReadOwnerRecordCountAsync(tenantB, TenantB)
                    .ConfigureAwait(false));
            Assert.Equal(
                1,
                await ReadTenantRevisionAsync(tenantB, TenantB)
                    .ConfigureAwait(false));
            Assert.Equal(
                tenantBUnitId,
                (await tenantB.InventoryUnits.SingleAsync()
                    .ConfigureAwait(false)).Id);
        }
    }

    private static async Task SeedGraphInScopeAsync(
        IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        await SeedGraphAsync(scope.ServiceProvider).ConfigureAwait(false);
    }

    private static async Task SeedDenseDestroyStateAsync(
        IServiceProvider services,
        TestClock clock)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext context = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();

        for (int allocationIndex = 0; allocationIndex < 5;
            allocationIndex++)
        {
            Guid[] units = Enumerable.Range(
                    allocationIndex * InventoryAllocation.MaximumUnits,
                    InventoryAllocation.MaximumUnits)
                .Select(index => DenseGuid(index, 0xd4))
                .ToArray();
            InventoryAllocation allocation =
                InventoryAllocation.CreateRejected(
                    DenseGuid(allocationIndex, 0xd1),
                    TenantA,
                    DenseGuid(allocationIndex, 0xd2),
                    DenseGuid(allocationIndex, 0xd3),
                    PropertyId,
                    DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
                    DateOnly.FromDateTime(
                        FrozenAtUtc.AddDays(1).UtcDateTime),
                    units,
                    InventoryAllocationRejection.AllocationConflict,
                    FrozenAtUtc.AddDays(-2)).Value;
            context.Allocations.Add(allocation);
        }

        InventoryPropertyTopology property = await context.PropertyTopology
            .SingleAsync(item => item.Id == PropertyId).ConfigureAwait(false);
        property.Apply(
            "Dense property",
            "dense",
            "UTC",
            PropertyStatus.Active,
            sourceVersion: 2);
        property.AdvanceAvailabilitySelection();
        InventoryRoomTopology room = InventoryRoomTopology.Create(
            RoomId,
            TenantA,
            PropertyId);
        room.Apply(
            PropertyId,
            "Dense room",
            "Main",
            "1",
            RoomStatus.Active,
            sourceVersion: 1);
        InventoryBedTopology bed = InventoryBedTopology.Create(
            BedId,
            TenantA,
            PropertyId,
            RoomId);
        bed.Apply(
            PropertyId,
            RoomId,
            "1",
            BedStatus.Active,
            sourceVersion: 1);

        OutboxMessage outbox = new(
            Guid.Parse("d0000000-0000-0000-0000-000000000001"),
            "bunkfy.inventory.termination-test.v1",
            "termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            "{}",
            clock.UtcNow);
        outbox.MarkClaimed(
            "inventory-test-worker",
            clock.UtcNow,
            TimeSpan.FromMinutes(1));

        context.RoomTopology.Add(room);
        context.BedTopology.Add(bed);
        context.OutboxMessages.Add(outbox);
        context.InboxMessages.Add(InboxMessage.Create(
            Guid.Parse("d1000000-0000-0000-0000-000000000001"),
            "inventory-termination-test-handler",
            "bunkfy.inventory.termination-test.v1",
            "inventory-termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            clock.UtcNow));
        context.ProjectionRebuildCheckpoints.Add(
            InventoryProjectionRebuildCheckpoint.Create(
                new ProjectionRebuildCheckpointKey(
                    InventoryModuleMetadata.Name,
                    Guid.Parse(
                        "d2000000-0000-0000-0000-000000000001"),
                    "inventory-topology",
                    TenantA),
                ProjectionRebuildCheckpoint.Start(
                    projectionVersion: 1,
                    clock.UtcNow)));
        await context.SaveChangesAsync().ConfigureAwait(false);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.allocation_operation_locks
                ("Id", "AllocationId", "Revision", "ScopeId")
            VALUES ({AllocationId}, {AllocationId}, 1, {TenantA});
            """).ConfigureAwait(false);
    }

    private static async Task AssertProofTriggersProtectNormalTrafficAsync(
        IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext context = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();

        Assert.Equal(
            1,
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.allocation_anonymisation_tombstones
                SET "Revision" = "Revision"
                WHERE "ScopeId" = {TenantA};
                """).ConfigureAwait(false));

        PostgresException receiptUpdate =
            await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE inventory.allocation_anonymisation_receipts
                    SET "ActorId" = "ActorId"
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptUpdate.SqlState);

        PostgresException receiptDelete =
            await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM inventory.allocation_anonymisation_restore_receipts
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptDelete.SqlState);

        PostgresException tombstoneDelete =
            await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM inventory.allocation_anonymisation_tombstones
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", tombstoneDelete.SqlState);
        Assert.Contains(
            "inventory anonymisation tombstones cannot be deleted",
            tombstoneDelete.MessageText,
            StringComparison.Ordinal);
    }

    private static TenantTerminationContributionRequest TenantDestroyRequest(
        WorkspaceTerminationFence fence) =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantA,
            fence.ProcessId,
            fence.CaseId,
            fence.ApprovalRevision,
            OperationRevision: 2,
            fence.TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("e0000000-0000-0000-0000-000000000001"),
            Guid.Parse("e1000000-0000-0000-0000-000000000001"),
            fence.PolicyEvidenceSha256,
            "termination-executor",
            ExportNowUtc.AddMinutes(30));

    private static Guid DenseGuid(int index, byte discriminator)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(index + 1).CopyTo(bytes, 0);
        bytes[15] = discriminator;
        return new Guid(bytes);
    }

    private static Task<long> ReadTenantRevisionAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "Revision" AS "Value"
            FROM inventory.tenant_revisions
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadAllocationUnitCountAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM inventory.allocation_units
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadOwnerRecordCountAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT (
                (SELECT COUNT(*) FROM inventory.outbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.inbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_anonymisation_restore_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_anonymisation_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_anonymisation_tombstones WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_amendment_decisions WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_units WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocations WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.manual_blocks WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.manual_block_groups WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.allocation_operation_locks WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.bed_retirements WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.room_retirements WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.room_configurations WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.inventory_units WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.bed_topology WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.room_topology WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.property_topology WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM inventory.projection_rebuild_checkpoints WHERE "ScopeId" = {tenantId})
            ) AS "Value"
            """).SingleAsync();

    private static Task<long> ReadOperationCountAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM inventory.tenant_destroy_operations
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadReceiptCountAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM inventory.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadReceiptRemovedCountAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "RemovedRecordCount" AS "Value"
            FROM inventory.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<int> ReadLifecycleStatusAsync(
        InventoryDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<int>($"""
            SELECT "LifecycleStatus" AS "Value"
            FROM inventory.tenant_revisions
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();
}
