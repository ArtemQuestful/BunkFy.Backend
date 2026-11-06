namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.TimeZones;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainDataHoldAction =
    BunkFy.Modules.Guests.Domain.Models.GuestDataHoldAction;

public sealed class GuestsTenantTerminationIntegrationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid CaseId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset ExportNowUtc =
        new(2026, 7, 31, 18, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        ExportNowUtc.AddMinutes(-1);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_repeatable_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guests_tenant_export_tests")
                .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        ProofIds proofIds;
        Guid tenantAGuestId;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            GuestsDbContext guests = seedScope.ServiceProvider
                .GetRequiredService<GuestsDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await guests.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            (tenantAGuestId, proofIds) = await SeedGraphAsync(
                guests,
                TenantA);
        }

        Guid tenantBGuestId;
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            GuestsDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<GuestsDbContext>();
            GuestProfile tenantBGuest = CreateProfile(
                TenantB,
                PropertyId,
                "Other tenant guest");
            tenantBGuestId = tenantBGuest.Id;
            tenantBContext.GuestProfiles.Add(tenantBGuest);
            await tenantBContext.SaveChangesAsync();
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
                    GuestsTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("guests.termination.exported", result.ResultCode);
        Assert.Contains(
            first.Records,
            record => record.RecordId == tenantAGuestId);
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == tenantBGuestId);
        Assert.Equal(
            GuestsTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());

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
        await AssertOwnerProofIsDatabaseProtectedAsync(
            scope.ServiceProvider,
            proofIds);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_destroy_is_bounded_resumable_immutable_and_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guests_tenant_destroy_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        TestClock tenantAClock = new(ExportNowUtc);
        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            connectionString,
            TenantA,
            tenantAClock);
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            GuestsDbContext guests = seedScope.ServiceProvider
                .GetRequiredService<GuestsDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await guests.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            await SeedGraphAsync(guests, TenantA);
        }

        WorkspaceTerminationFence tenantAFence =
            await AddFenceAsync(tenantAProvider, TenantA);
        using (IServiceScope blockedScope = tenantAProvider.CreateScope())
        {
            ITenantTerminationContributor contributor =
                ResolveContributor(blockedScope.ServiceProvider);
            TenantTerminationContributionResult blocked =
                await contributor.ExecuteAsync(
                    TenantDestroyRequest(
                        tenantAFence,
                        TenantA,
                        Guid.Parse(
                            "a1000000-0000-0000-0000-000000000001")),
                    CancellationToken.None);

            Assert.Equal(
                TenantTerminationContributionStatus.Blocked,
                blocked.Status);
            Assert.Equal(
                "guests.termination.destroy-active-hold",
                blocked.ResultCode);
            Assert.Equal(1, blocked.RemainingActiveCount);
        }

        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "guests.tenant_destroy_operations",
                TenantA));

        TestClock tenantBClock = new(ExportNowUtc);
        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB,
            tenantBClock);
        using (IServiceScope seedScope = tenantBProvider.CreateScope())
        {
            GuestsDbContext guests = seedScope.ServiceProvider
                .GetRequiredService<GuestsDbContext>();
            await SeedGraphAsync(guests, TenantB);
            GuestDataHold hold = await guests.DataHolds
                .SingleAsync(candidate =>
                    candidate.State == GuestDataHoldState.Active);
            Assert.True(hold.Release(
                hold.Version,
                "user:privacy-controller",
                ExportNowUtc.AddMinutes(-1)).IsSuccess);
            await guests.SaveChangesAsync();

            OutboxMessage[] messages = Enumerable.Range(0, 501)
                .Select(_ => new OutboxMessage(
                    Guid.NewGuid(),
                    "bunkfy.guests.termination-test.v1",
                    "termination-test",
                    version: 1,
                    TenantB,
                    tenantBClock.UtcNow,
                    "{}",
                    tenantBClock.UtcNow))
                .ToArray();
            messages[0].MarkClaimed(
                "termination-test-worker",
                tenantBClock.UtcNow,
                TimeSpan.FromMinutes(1));
            guests.OutboxMessages.AddRange(messages);
            await guests.SaveChangesAsync();
        }

        long selectedRevision = await ScalarForTenantAsync(
            connectionString,
            """
            SELECT "Revision"::bigint
            FROM guests.tenant_revisions
            WHERE "ScopeId" = @tenantId
            """,
            TenantB);

        WorkspaceTerminationFence tenantBFence =
            await AddFenceAsync(tenantBProvider, TenantB);
        Guid operationId = Guid.Parse(
            "b1000000-0000-0000-0000-000000000001");
        TenantTerminationContributionRequest request = TenantDestroyRequest(
            tenantBFence,
            TenantB,
            operationId);
        using IServiceScope destroyScope = tenantBProvider.CreateScope();
        ITenantTerminationContributor destroyContributor =
            ResolveContributor(destroyScope.ServiceProvider);

        TenantTerminationContributionResult busy =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            busy.Status);
        Assert.Equal(
            "guests.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "guests.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            501,
            await CountForTenantAsync(
                connectionString,
                "guests.outbox_messages",
                TenantB));

        tenantBClock.UtcNow = tenantBClock.UtcNow.AddMinutes(2);
        TenantTerminationContributionResult firstBatch =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            firstBatch.Status);
        Assert.Equal(
            "guests.termination.destroy-in-progress",
            firstBatch.ResultCode);
        Assert.Equal(500, firstBatch.AffectedCount);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "guests.outbox_messages",
                TenantB));

        TenantTerminationContributionResult completed = firstBatch;
        int attempts = 1;
        while (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 100)
        {
            completed = await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            completed.Status);
        Assert.Equal("guests.termination.destroyed", completed.ResultCode);
        Assert.True(completed.AffectedCount > 501);
        Assert.Equal(selectedRevision, completed.SelectedProofRevision);
        Assert.Equal(
            selectedRevision + 1,
            completed.ResultingProofRevision);
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "guests.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "guests.tenant_destroy_receipts",
                TenantB));
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "guests.guest_profiles",
                TenantB));
        Assert.Equal(
            0,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT (
                    (SELECT COUNT(*) FROM guests.guest_data_rights_correction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.guest_processing_restriction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.data_hold_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.guest_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.guest_anonymisation_restore_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.guest_retention_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM guests.guest_anonymisation_tombstones WHERE "ScopeId" = @tenantId)
                )::bigint
                """,
                TenantB));
        Assert.Equal(
            3,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM guests.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantB));

        TenantTerminationContributionResult replay =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(completed, replay);
        TenantTerminationContributionResult conflict =
            await destroyContributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);

        GuestsDbContext closedContext = destroyScope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        closedContext.ChangeTracker.Clear();
        closedContext.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.guests.termination-test.v1",
            "termination-test",
            version: 1,
            TenantB,
            tenantBClock.UtcNow,
            "{}",
            tenantBClock.UtcNow));
        InvalidOperationException closedFailure =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => closedContext.SaveChangesAsync());
        Assert.Equal(
            "The workspace is not accepting Guests mutations.",
            closedFailure.Message);
        closedContext.ChangeTracker.Clear();

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                closedContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE guests.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantB};
                    """));
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "guest data-rights receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        Assert.True(await CountForTenantAsync(
            connectionString,
            "guests.guest_profiles",
            TenantA) > 0);
        Assert.Equal(
            1,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM guests.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantA));
    }

    private static async Task<(Guid GuestId, ProofIds ProofIds)>
        SeedGraphAsync(
            GuestsDbContext context,
            string tenantId)
    {
        GuestProfile profile = CreateProfile(
            tenantId,
            PropertyId,
            "Maya Chen");
        context.GuestProfiles.Add(profile);
        long managementExpectedVersion = profile.Version;
        DateTimeOffset managementCompletedAtUtc =
            ExportNowUtc.AddMinutes(-9);
        Assert.True(profile.Update(
            "Maya Chen Updated",
            "Maya Chen Legal",
            "guest@example.test",
            "+44 20 1234 5678",
            new DateOnly(1990, 2, 3),
            "GB",
            "en-GB",
            "Prefers a lower bunk.",
            managementExpectedVersion,
            "user:owner",
            Guid.NewGuid(),
            managementCompletedAtUtc).IsSuccess);
        context.ManagementOperations.Add(new GuestManagementOperation(
            new GuestManagementOperationRecord(
                Guid.NewGuid(),
                tenantId,
                PropertyId,
                profile.Id,
                GuestManagementOperationKind.Update,
                managementExpectedVersion,
                Digest,
                GuestStatus.Active,
                profile.Version,
                managementCompletedAtUtc)));
        GuestDataRightsCorrectionReceipt correction =
            GuestDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                PropertyId,
                CaseId,
                approvalRevision: 1,
                profile.Id,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [GuestProfileField.DisplayName],
                Guid.NewGuid(),
                Guid.NewGuid(),
                ExportNowUtc.AddMinutes(-8)).Value;
        context.DataRightsCorrectionReceipts.Add(correction);

        GuestProcessingRestriction restriction =
            GuestProcessingRestriction.Create(
                Guid.NewGuid(),
                tenantId,
                PropertyId,
                profile.Id,
                CaseId,
                applyApprovalRevision: 1,
                applySelectedGuestVersion: profile.Version,
                "user:owner",
                ExportNowUtc.AddMinutes(-7)).Value;
        GuestProcessingRestrictionReceipt restrictionReceipt =
            GuestProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                restriction.Id,
                GuestProcessingRestrictionAction.Apply,
                PropertyId,
                profile.Id,
                CaseId,
                approvalRevision: 1,
                selectedGuestVersion: profile.Version,
                GuestProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: restriction.Version,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:owner",
                Guid.NewGuid(),
                ExportNowUtc.AddMinutes(-7)).Value;
        GuestProcessingRestrictionProjection restrictionProjection =
            GuestProcessingRestrictionProjection.Create(
                tenantId,
                PropertyId,
                profile.Id,
                GuestProcessingRestrictionContract.CurrentVersion,
                ExportNowUtc.AddMinutes(-7)).Value;
        Assert.True(restrictionProjection.Apply(
            expectedRevision: 0,
            GuestProcessingRestrictionContract.CurrentVersion,
            ExportNowUtc.AddMinutes(-7)).IsSuccess);
        context.ProcessingRestrictionProjections.Add(restrictionProjection);
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionReceipts.Add(restrictionReceipt);

        GuestDataHold hold = GuestDataHold.Place(
            Guid.NewGuid(),
            tenantId,
            PropertyId,
            profile.Id,
            "regulatory-request",
            "user:owner",
            ExportNowUtc.AddMinutes(-6)).Value;
        GuestDataHoldReceipt holdReceipt = GuestDataHoldReceipt.Create(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            hold,
            DomainDataHoldAction.Place,
            profile.Version,
            "user:owner",
            ExportNowUtc.AddMinutes(-6)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(holdReceipt);

        (GuestAnonymisationReceipt anonymisationReceipt,
            GuestAnonymisationTombstone tombstone,
            GuestAnonymisationRestoreReceipt restoreReceipt) =
            SeedAnonymisationProof(context, tenantId);
        GuestRetentionAnonymisationReceipt retentionReceipt =
            SeedRetentionProof(context, tenantId);

        await context.SaveChangesAsync();
        return (
            profile.Id,
            new(
                correction.Id,
                restrictionReceipt.Id,
                holdReceipt.Id,
                anonymisationReceipt.Id,
                restoreReceipt.Id,
                retentionReceipt.Id,
                tombstone.Id));
    }

    private static (
        GuestAnonymisationReceipt Receipt,
        GuestAnonymisationTombstone Tombstone,
        GuestAnonymisationRestoreReceipt RestoreReceipt)
        SeedAnonymisationProof(
            GuestsDbContext context,
            string tenantId)
    {
        GuestProfile profile = CreateProfile(
            tenantId,
            OtherPropertyId,
            "Anonymised Guest Source");
        GuestProfileAnonymisationOutcome outcome = profile.Anonymise(
            profile.Version,
            "user:privacy-executor",
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-5)).Value;
        GuestAnonymisationReceipt receipt =
            GuestAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                OtherPropertyId,
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                profile.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                Digest,
                Digest,
                outcome.EventId,
                "user:privacy-executor",
                ExportNowUtc.AddMinutes(-5)).Value;
        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.Create(
                tenantId,
                profile.Id,
                receipt.CompletedAtUtc,
                receipt.CanonicalSha256).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = ExportNowUtc.AddMinutes(-4);
        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            receipt.CompletedAtUtc,
            receipt.CanonicalSha256,
            replayedAtUtc).IsSuccess);
        GuestAnonymisationRestoreReceipt restoreReceipt =
            GuestAnonymisationRestoreReceipt.Create(
                tenantId,
                ledgerEntryId,
                profile.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingGuestVersion,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.GuestProfiles.Add(profile);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
        return (receipt, tombstone, restoreReceipt);
    }

    private static GuestRetentionAnonymisationReceipt SeedRetentionProof(
        GuestsDbContext context,
        string tenantId)
    {
        GuestProfile profile = CreateProfile(
            tenantId,
            PropertyId,
            "Retention Guest");
        GuestRetentionExecution execution =
            GuestRetentionExecution.Start(
                Guid.NewGuid(),
                tenantId,
                "guest-operational",
                GuestRetentionExecution.MinimumRunningPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                ExportNowUtc.AddMinutes(-5),
                ExportNowUtc.AddMinutes(5)).Value;
        GuestProfileAnonymisationOutcome outcome =
            profile.AnonymiseForRetention(
                profile.Version,
                "system:retention",
                Guid.NewGuid(),
                ExportNowUtc.AddMinutes(-3)).Value;
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                execution.Id,
                profile.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                ExportNowUtc.AddDays(-1),
                Digest,
                TimeZoneCatalog.Default.CatalogVersion,
                outcome.EventId,
                "system:retention",
                ExportNowUtc.AddMinutes(-3)).Value;
        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                tenantId,
                receipt).Value;

        context.GuestProfiles.Add(profile);
        context.RetentionExecutions.Add(execution);
        context.RetentionAnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        return receipt;
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
            "The workspace is not accepting Guests mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices)
    {
        using IServiceScope scope = rootServices.CreateScope();
        GuestsDbContext context = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        context.GuestProfiles.Add(CreateProfile(
            TenantA,
            PropertyId,
            "Blocked Guest"));
        await context.SaveChangesAsync();
    }

    private static async Task AssertOwnerProofIsDatabaseProtectedAsync(
        IServiceProvider services,
        ProofIds ids)
    {
        GuestsDbContext context = services
            .GetRequiredService<GuestsDbContext>();
        await AssertTriggerRejectedAsync(
            context,
            $"""
            UPDATE guests.guest_data_rights_correction_receipts
            SET "ApprovalRevision" = "ApprovalRevision"
            WHERE "Id" = {ids.CorrectionReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM guests.guest_processing_restriction_receipts
            WHERE "Id" = {ids.RestrictionReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM guests.data_hold_receipts
            WHERE "Id" = {ids.HoldReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM guests.guest_anonymisation_receipts
            WHERE "Id" = {ids.AnonymisationReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM guests.guest_anonymisation_restore_receipts
            WHERE "Id" = {ids.RestoreReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM guests.guest_retention_anonymisation_receipts
            WHERE "Id" = {ids.RetentionReceiptId};
            """);

        PostgresException tombstoneDelete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM guests.guest_anonymisation_tombstones
                WHERE "Id" = {ids.TombstoneId};
                """));
        Assert.Equal("P0001", tombstoneDelete.SqlState);
        Assert.Contains(
            "guest anonymisation tombstones cannot be deleted",
            tombstoneDelete.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task AssertTriggerRejectedAsync(
        GuestsDbContext context,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains(
            "guest data-rights receipts are append-only",
            failure.MessageText,
            StringComparison.Ordinal);
    }

    private static GuestProfile CreateProfile(
        string tenantId,
        Guid propertyId,
        string displayName) =>
        GuestProfile.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            displayName,
            $"{displayName} Legal",
            "guest@example.test",
            "+44 20 1234 5678",
            new DateOnly(1990, 2, 3),
            "GB",
            "en-GB",
            "Prefers a lower bunk.",
            "user:owner",
            Guid.NewGuid(),
            ExportNowUtc.AddDays(-1)).Value;

    private static WorkspaceTerminationFence CreateTerminationFence() =>
        CreateTerminationFence(TenantA);

    private static WorkspaceTerminationFence CreateTerminationFence(
        string tenantId) =>
        WorkspaceTerminationFence.Freeze(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 1,
            Guid.NewGuid(),
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;

    private static async Task<WorkspaceTerminationFence> AddFenceAsync(
        ServiceProvider provider,
        string tenantId)
    {
        using IServiceScope scope = provider.CreateScope();
        WorkspacesDbContext context = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence(tenantId);
        context.WorkspaceTerminationFences.Add(fence);
        await context.SaveChangesAsync();
        return fence;
    }

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

    private static TenantTerminationContributionRequest TenantDestroyRequest(
        WorkspaceTerminationFence fence,
        string tenantId,
        Guid idempotencyKey) =>
        new(
            TenantTerminationContract.CurrentVersion,
            tenantId,
            fence.ProcessId,
            fence.CaseId,
            fence.ApprovalRevision,
            OperationRevision: 2,
            fence.TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.NewGuid(),
            idempotencyKey,
            fence.PolicyEvidenceSha256,
            "termination-executor",
            ExportNowUtc.AddHours(2));

    private static ITenantTerminationContributor ResolveContributor(
        IServiceProvider services) =>
        services.GetServices<ITenantTerminationContributor>()
            .Single(contributor => string.Equals(
                contributor.Descriptor.OwnerKey,
                GuestsTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal));

    private static Task<long> CountForTenantAsync(
        string connectionString,
        string qualifiedTable,
        string tenantId) =>
        ScalarForTenantAsync(
            connectionString,
            $"SELECT COUNT(*) FROM {qualifiedTable} " +
            "WHERE \"ScopeId\" = @tenantId",
            tenantId);

    private static async Task<long> ScalarForTenantAsync(
        string connectionString,
        string commandText,
        string tenantId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(commandText, connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

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
        builder.Services.AddSingleton<IIdGenerator, TestIdGenerator>();
        builder.AddWorkspacesPersistence();
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddGuestsPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed record ProofIds(
        Guid CorrectionReceiptId,
        Guid RestrictionReceiptId,
        Guid HoldReceiptId,
        Guid AnonymisationReceiptId,
        Guid RestoreReceiptId,
        Guid RetentionReceiptId,
        Guid TombstoneId);

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
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
