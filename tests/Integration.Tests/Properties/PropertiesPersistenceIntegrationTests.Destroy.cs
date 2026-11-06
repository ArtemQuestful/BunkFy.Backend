namespace Integration.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainGovernanceAcknowledgement =
    BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

public sealed partial class PropertiesPersistenceIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_destroy_is_bounded_immutable_and_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_destroy_tests")
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
                .GetRequiredService<PropertiesDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
        }

        (Property property, Room room) =
            await SeedDenseDestroyStateAsync(tenantAProvider, clock)
                .ConfigureAwait(false);
        using (IServiceScope proofScope = tenantAProvider.CreateScope())
        {
            await AssertGovernanceHistoryIsAppendOnlyAsync(
                    proofScope.ServiceProvider,
                    property.Id)
                .ConfigureAwait(false);
        }

        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB);
        Guid tenantBPropertyId;
        Guid tenantBRoomId;
        Guid tenantBBedId;
        using (IServiceScope tenantBSeedScope = tenantBProvider.CreateScope())
        {
            (tenantBPropertyId, tenantBRoomId, tenantBBedId) =
                await SeedOtherTenantGraphAsync(
                    tenantBSeedScope.ServiceProvider)
                    .ConfigureAwait(false);
        }

        WorkspaceTerminationFence fence = CreateTerminationFence();
        using IServiceScope inFlightScope = tenantAProvider.CreateScope();
        PropertiesDbContext inFlight = inFlightScope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        await using IDbContextTransaction inFlightTransaction =
            await inFlight.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        Property inFlightProperty = await inFlight.Properties
            .SingleAsync(candidate => candidate.Id == property.Id)
            .ConfigureAwait(false);
        Assert.True(inFlightProperty.Update(
            "Hostel One updated",
            inFlightProperty.Code.Value,
            inFlightProperty.TimeZoneId.Value,
            inFlightProperty.Version,
            Guid.NewGuid(),
            clock.UtcNow).IsSuccess);
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
            PropertiesDbContext properties = revisionScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            selectedRevision = await ReadTenantRevisionAsync(
                    properties,
                    TenantA)
                .ConfigureAwait(false);
        }

        using IServiceScope ownerScope = tenantAProvider.CreateScope();
        ITenantTerminationContributor contributor = ownerScope.ServiceProvider
            .GetServices<ITenantTerminationContributor>()
            .Single(candidate => candidate.Descriptor.OwnerKey ==
                PropertiesTenantTerminationMetadata.OwnerKey);
        TenantTerminationContributionRequest request =
            TenantDestroyRequest(fence);

        TenantTerminationContributionResult busy =
            await contributor.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            busy.Status);
        Assert.Equal(
            "properties.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(0, busy.AffectedCount);

        PropertiesDbContext ownerContext = ownerScope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        Assert.Equal(
            1,
            await ReadDestroyOperationCountAsync(ownerContext, TenantA)
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
                    PropertiesModuleMetadata.Name);
            IReadOnlyList<OutboxMessageRecord> claims =
                await outbox.ClaimPendingAsync(
                    batchSize: 10,
                    "properties-claim-test",
                    clock.UtcNow,
                    TimeSpan.FromMinutes(1),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.Empty(claims);
        }

        long[] expectedEarlyCounts = [1, 2, 3, 4];
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
                "properties.termination.destroy-in-progress",
                progress.ResultCode);
            Assert.Equal(expected, progress.AffectedCount);
        }

        TenantTerminationContributionResult firstBedBatch =
            await contributor.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            firstBedBatch.Status);
        Assert.Equal(504, firstBedBatch.AffectedCount);
        Assert.Equal(
            1,
            await ReadBedCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        TenantTerminationContributionResult finalBedBatch =
            await contributor.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            finalBedBatch.Status);
        Assert.Equal(505, finalBedBatch.AffectedCount);
        Assert.Equal(
            0,
            await ReadBedCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        TenantTerminationContributionResult completed = finalBedBatch;
        int attempts = 0;
        while (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 10)
        {
            completed = await contributor.ExecuteAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            completed.Status);
        Assert.Equal(
            "properties.termination.destroyed",
            completed.ResultCode);
        Assert.Equal(510, completed.AffectedCount);
        Assert.Equal(selectedRevision, completed.SelectedProofRevision);
        Assert.Equal(
            selectedRevision + 1,
            completed.ResultingProofRevision);

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(completed, replay);
        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "properties.termination.destroy-conflict",
            conflict.ResultCode);

        Assert.Equal(
            0,
            await ReadOwnerRecordCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await ReadDestroyOperationCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadDestroyReceiptCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            510,
            await ReadDestroyReceiptRemovedCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            3,
            await ReadLifecycleStatusAsync(ownerContext, TenantA)
                .ConfigureAwait(false));

        ownerContext.ChangeTracker.Clear();
        ownerContext.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.properties.termination-test.v1",
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
            "The workspace is not accepting Properties mutations.",
            closed.Message);
        ownerContext.ChangeTracker.Clear();

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ownerContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "properties tenant destruction receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        using (IServiceScope tenantBVerificationScope =
            tenantBProvider.CreateScope())
        {
            PropertiesDbContext tenantB =
                tenantBVerificationScope.ServiceProvider
                    .GetRequiredService<PropertiesDbContext>();
            Assert.Equal(
                8,
                await ReadOwnerRecordCountAsync(tenantB, TenantB)
                    .ConfigureAwait(false));
            Assert.Equal(
                2,
                await ReadTenantRevisionAsync(tenantB, TenantB)
                    .ConfigureAwait(false));
            Assert.Equal(
                tenantBPropertyId,
                (await tenantB.Properties.SingleAsync()
                    .ConfigureAwait(false)).Id);
            Room tenantBRoom = await tenantB.Rooms
                .Include(candidate => candidate.Beds)
                .SingleAsync()
                .ConfigureAwait(false);
            Assert.Equal(tenantBRoomId, tenantBRoom.Id);
            Assert.Equal(tenantBBedId, Assert.Single(tenantBRoom.Beds).Id);
        }
    }

    private static async Task<(Property Property, Room Room)>
        SeedDenseDestroyStateAsync(
            IServiceProvider services,
            TestClock clock)
    {
        using IServiceScope scope = services.CreateScope();
        (Property property, Room room) =
            await SeedExportGraphAsync(scope.ServiceProvider)
                .ConfigureAwait(false);
        PropertiesDbContext context = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        room = await context.Rooms
            .Include(candidate => candidate.Beds)
            .SingleAsync(candidate => candidate.Id == room.Id)
            .ConfigureAwait(false);
        for (int index = room.Beds.Count; index < 501; index++)
        {
            Assert.True(room.AddBed(
                DenseGuid(index, 0xd1),
                $"Dense {index + 1}",
                room.Version,
                DenseGuid(index, 0xd2),
                clock.UtcNow).IsSuccess);
        }

        OutboxMessage outbox = new(
            Guid.Parse("d0000000-0000-0000-0000-000000000001"),
            "bunkfy.properties.termination-test.v1",
            "termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            "{}",
            clock.UtcNow);
        outbox.MarkClaimed(
            "properties-test-worker",
            clock.UtcNow,
            TimeSpan.FromMinutes(1));
        context.OutboxMessages.Add(outbox);
        context.InboxMessages.Add(InboxMessage.Create(
            Guid.Parse("d1000000-0000-0000-0000-000000000001"),
            "properties-termination-test-handler",
            "bunkfy.properties.termination-test.v1",
            "properties-termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            clock.UtcNow));
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (property, room);
    }

    private static async Task<(Guid PropertyId, Guid RoomId, Guid BedId)>
        SeedOtherTenantGraphAsync(IServiceProvider services)
    {
        PropertiesDbContext context = services
            .GetRequiredService<PropertiesDbContext>();
        IPropertyGovernanceRevisionWriter revisionWriter = services
            .GetRequiredService<IPropertyGovernanceRevisionWriter>();
        IPropertyRepository properties = services
            .GetRequiredService<IPropertyRepository>();
        IRoomRepository rooms = services
            .GetRequiredService<IRoomRepository>();
        Property property = CreateProperty(
            "other-hostel",
            "Other Hostel",
            TenantB);
        await properties.AddAsync(property, CancellationToken.None)
            .ConfigureAwait(false);
        await AppendCreatedTimeZoneOperationAsync(
                services,
                property,
                "user:other-owner")
            .ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

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
            "user:other-owner").IsSuccess);
        Room room = Room.Create(
            Guid.Parse("b1000000-0000-0000-0000-000000000001"),
            TenantB,
            property.Id,
            "201",
            null,
            null,
            Guid.NewGuid(),
            FrozenAtUtc).Value;
        Guid bedId = Guid.Parse(
            "b2000000-0000-0000-0000-000000000001");
        Assert.True(room.AddBed(
            bedId,
            "A",
            room.Version,
            Guid.NewGuid(),
            FrozenAtUtc).IsSuccess);
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
        await rooms.AddAsync(room, CancellationToken.None)
            .ConfigureAwait(false);
        await revisionWriter.AppendAsync(
            new PropertyGovernanceRevisionWriteModel(
                Guid.Parse("b3000000-0000-0000-0000-000000000001"),
                TenantB,
                property.Id,
                property.Version,
                PropertyGovernanceRevisionAction.Activated,
                "created",
                Previous: null,
                Current: current,
                "user:other-owner",
                FrozenAtUtc),
            CancellationToken.None).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (property.Id, room.Id, bedId);
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
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "Revision" AS "Value"
            FROM properties.tenant_revisions
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadBedCountAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM properties.beds
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadOwnerRecordCountAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT (
                (SELECT COUNT(*) FROM properties.outbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.inbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.property_governance_revisions WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*)
                    FROM properties.property_governance_acknowledgements acknowledgement
                    INNER JOIN properties.properties property
                        ON property."Id" = acknowledgement."PropertyId"
                    WHERE property."ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.beds WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.rooms WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.property_time_zone_operations WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.properties WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.property_operation_locks WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM properties.room_operation_locks WHERE "ScopeId" = {tenantId})
            ) AS "Value"
            """).SingleAsync();

    private static Task<long> ReadDestroyOperationCountAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM properties.tenant_destroy_operations
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadDestroyReceiptCountAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM properties.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadDestroyReceiptRemovedCountAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "RemovedRecordCount" AS "Value"
            FROM properties.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<int> ReadLifecycleStatusAsync(
        PropertiesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<int>($"""
            SELECT "LifecycleStatus" AS "Value"
            FROM properties.tenant_revisions
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();
}
