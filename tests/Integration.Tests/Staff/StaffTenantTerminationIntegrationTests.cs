namespace Integration.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
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

public sealed class StaffTenantTerminationIntegrationTests
{
    private const string TenantA =
        "10000000-0000-0000-0000-000000000001";
    private const string TenantB =
        "10000000-0000-0000-0000-000000000002";
    private const string TenantSparseAnchor =
        "10000000-0000-0000-0000-000000000003";
    private const string TenantSingleResolution =
        "10000000-0000-0000-0000-000000000004";
    private const string TenantPagedResolutions =
        "10000000-0000-0000-0000-000000000005";
    private const string TenantIdentityAnchorSentinel =
        "10000000-0000-0000-0000-000000000006";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SeedNowUtc =
        new(2026, 7, 31, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        SeedNowUtc.AddMinutes(20);
    private static readonly DateTimeOffset ExportNowUtc =
        SeedNowUtc.AddMinutes(25);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_repeatable_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_tenant_export_tests")
                .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        ProofIds proofIds;
        Guid tenantAStaffId;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            StaffDbContext staff = seedScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await staff.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            (tenantAStaffId, proofIds) = await SeedGraphAsync(
                staff,
                seedScope.ServiceProvider.GetRequiredService<
                    IStaffMemberMutationOperationRepository>(),
                TenantA);
            await SeedIdentityAnchorRecordsAsync(
                staff, seedScope.ServiceProvider, TenantA, tenantAStaffId,
                anchorCount: 1, resolutionCount: 1);
        }

        Guid tenantBStaffId;
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            StaffDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            StaffMember tenantBStaff = CreateMember(
                TenantB,
                "Other tenant staff");
            tenantBStaffId = tenantBStaff.Id;
            tenantBContext.StaffMembers.Add(tenantBStaff);
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
                    StaffTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("staff.termination.exported", result.ResultCode);
        Assert.Contains(
            first.Records,
            record => record.RecordId == tenantAStaffId);
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == tenantBStaffId);
        Assert.Equal(
            StaffTenantTerminationMetadata.RecordTypes,
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
                .WithDatabase("bunkfy_staff_tenant_destroy_tests")
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
            StaffDbContext staff = seedScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await staff.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            await SeedGraphAsync(
                staff,
                seedScope.ServiceProvider.GetRequiredService<
                    IStaffMemberMutationOperationRepository>(),
                TenantA);
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
                "staff.termination.destroy-active-hold",
                blocked.ResultCode);
            Assert.Equal(1, blocked.RemainingActiveCount);
        }

        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "staff.tenant_destroy_operations",
                TenantA));

        TestClock tenantBClock = new(ExportNowUtc);
        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB,
            tenantBClock);
        using (IServiceScope seedScope = tenantBProvider.CreateScope())
        {
            StaffDbContext staff = seedScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            await SeedGraphAsync(
                staff,
                seedScope.ServiceProvider.GetRequiredService<
                    IStaffMemberMutationOperationRepository>(),
                TenantB);
            StaffDataHold hold = await staff.DataHolds
                .SingleAsync(candidate =>
                    candidate.State == StaffDataHoldState.Active);
            Assert.True(hold.Release(
                hold.Version,
                "user:privacy-controller",
                ExportNowUtc.AddMinutes(-1)).IsSuccess);
            await staff.SaveChangesAsync();

            OutboxMessage[] messages = Enumerable.Range(0, 501)
                .Select(_ => new OutboxMessage(
                    Guid.NewGuid(),
                    "bunkfy.staff.termination-test.v1",
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
            staff.OutboxMessages.AddRange(messages);
            await staff.SaveChangesAsync();
        }

        long selectedRevision = await ScalarForTenantAsync(
            connectionString,
            """
            SELECT "Revision"::bigint
            FROM staff.tenant_revisions
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
            "staff.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            501,
            await CountForTenantAsync(
                connectionString,
                "staff.outbox_messages",
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
            "staff.termination.destroy-in-progress",
            firstBatch.ResultCode);
        Assert.Equal(500, firstBatch.AffectedCount);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.outbox_messages",
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
        Assert.Equal("staff.termination.destroyed", completed.ResultCode);
        Assert.True(completed.AffectedCount > 501);
        Assert.Equal(selectedRevision, completed.SelectedProofRevision);
        Assert.Equal(
            selectedRevision + 1,
            completed.ResultingProofRevision);
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "staff.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.tenant_destroy_receipts",
                TenantB));
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "staff.staff_members",
                TenantB));
        Assert.Equal(
            0,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT (
                    (SELECT COUNT(*) FROM staff.data_rights_correction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_processing_restriction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_employment_governance_change_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_data_hold_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_anonymisation_restore_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_retention_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM staff.staff_anonymisation_tombstones WHERE "ScopeId" = @tenantId)
                )::bigint
                """,
                TenantB));
        Assert.Equal(
            3,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM staff.tenant_revisions
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

        StaffDbContext closedContext = destroyScope.ServiceProvider
            .GetRequiredService<StaffDbContext>();
        closedContext.ChangeTracker.Clear();
        closedContext.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.staff.termination-test.v1",
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
            "The workspace is not accepting Staff mutations.",
            closedFailure.Message);
        closedContext.ChangeTracker.Clear();

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                closedContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE staff.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantB};
                    """));
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "Staff receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        Assert.True(await CountForTenantAsync(
            connectionString,
            "staff.staff_members",
            TenantA) > 0);
        Assert.Equal(
            1,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM staff.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantA));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_destroy_persists_sparse_and_paged_identity_anchor_stages()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_anchor_destroy_stage_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        using (ServiceProvider sentinelProvider = CreatePersistenceProvider(
                   connectionString,
                   TenantIdentityAnchorSentinel,
                   new TestClock(ExportNowUtc)))
        {
            await SeedIdentityAnchorGraphAsync(
                    sentinelProvider,
                    TenantIdentityAnchorSentinel,
                    anchorCount: 1,
                    resolutionCount: 1)
                .ConfigureAwait(false);
        }

        DestroyStageSnapshot[] sparse =
            await DestroyIdentityAnchorScenarioAsync(
                connectionString,
                TenantSparseAnchor,
                anchorCount: 1,
                resolutionCount: 0).ConfigureAwait(false);
        DestroyStageSnapshot sparseAnchorBatch = Assert.Single(
            sparse,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 17 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 0 &&
                snapshot.StaffMemberCount == 1);
        Assert.Equal(17, sparseAnchorBatch.Stage);

        DestroyStageSnapshot[] single =
            await DestroyIdentityAnchorScenarioAsync(
                connectionString,
                TenantSingleResolution,
                anchorCount: 1,
                resolutionCount: 1).ConfigureAwait(false);
        Assert.Contains(
            single,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 24 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 1);
        Assert.Contains(
            single,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 17 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 0);

        DestroyStageSnapshot[] paged =
            await DestroyIdentityAnchorScenarioAsync(
                connectionString,
                TenantPagedResolutions,
                anchorCount: 501,
                resolutionCount: 501).ConfigureAwait(false);
        Assert.Contains(
            paged,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 25 &&
                snapshot.ResolutionCount == 1 &&
                snapshot.AnchorCount == 501);
        Assert.Contains(
            paged,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 24 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 501);
        Assert.Contains(
            paged,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 24 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 1);
        Assert.Contains(
            paged,
            snapshot => snapshot.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                snapshot.Stage == 17 &&
                snapshot.ResolutionCount == 0 &&
                snapshot.AnchorCount == 0);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.identity_provisioning_anchor_resolutions",
                TenantIdentityAnchorSentinel).ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.identity_provisioning_anchors",
                TenantIdentityAnchorSentinel).ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.staff_members",
                TenantIdentityAnchorSentinel).ConfigureAwait(false));
        Assert.Equal(
            1,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM staff.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantIdentityAnchorSentinel).ConfigureAwait(false));
    }

    private static async Task<DestroyStageSnapshot[]>
        DestroyIdentityAnchorScenarioAsync(
            string connectionString,
            string tenantId,
            int anchorCount,
            int resolutionCount)
    {
        TestClock clock = new(ExportNowUtc);
        using ServiceProvider provider = CreatePersistenceProvider(
            connectionString,
            tenantId,
            clock);
        await SeedIdentityAnchorGraphAsync(
                provider,
                tenantId,
                anchorCount,
                resolutionCount)
            .ConfigureAwait(false);

        WorkspaceTerminationFence fence = await AddFenceAsync(
            provider,
            tenantId).ConfigureAwait(false);
        TenantTerminationContributionRequest request = TenantDestroyRequest(
            fence,
            tenantId,
            DeterministicSourceId(tenantId, 10_000));
        long selectedRevision = await ScalarForTenantAsync(
            connectionString,
            """
            SELECT "Revision"
            FROM staff.tenant_revisions
            WHERE "ScopeId" = @tenantId
            """,
            tenantId).ConfigureAwait(false);
        using IServiceScope destroyScope = provider.CreateScope();
        ITenantTerminationContributor contributor = ResolveContributor(
            destroyScope.ServiceProvider);
        List<DestroyStageSnapshot> snapshots = [];
        for (int attempt = 0; attempt < 20; attempt++)
        {
            TenantTerminationContributionResult result =
                await contributor.ExecuteAsync(
                    request,
                    CancellationToken.None).ConfigureAwait(false);
            snapshots.Add(new(
                result.Status,
                await ReadDestroyStageAsync(
                    connectionString,
                    tenantId).ConfigureAwait(false),
                await CountForTenantAsync(
                    connectionString,
                    "staff.identity_provisioning_anchor_resolutions",
                    tenantId).ConfigureAwait(false),
                await CountForTenantAsync(
                    connectionString,
                    "staff.identity_provisioning_anchors",
                    tenantId).ConfigureAwait(false),
                await CountForTenantAsync(
                    connectionString,
                    "staff.staff_members",
                    tenantId).ConfigureAwait(false)));
            if (result.Status == TenantTerminationContributionStatus.Completed)
            {
                break;
            }

            Assert.Equal(
                TenantTerminationContributionStatus.RetryRequired,
                result.Status);
        }

        DestroyStageSnapshot completed = Assert.Single(
            snapshots,
            snapshot => snapshot.Status ==
                TenantTerminationContributionStatus.Completed);
        Assert.Null(completed.Stage);
        Assert.Equal(0, completed.ResolutionCount);
        Assert.Equal(0, completed.AnchorCount);
        Assert.Equal(0, completed.StaffMemberCount);
        Assert.Equal(
            3,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM staff.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                tenantId).ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "staff.tenant_destroy_receipts",
                tenantId).ConfigureAwait(false));
        DestroyReceiptSnapshot receipt = await ReadDestroyReceiptAsync(
            connectionString,
            tenantId).ConfigureAwait(false);
        Assert.Equal(request.IdempotencyKey, receipt.OperationId);
        Assert.Equal(tenantId, receipt.ScopeId);
        Assert.Equal(selectedRevision, receipt.SelectedRevision);
        Assert.Equal(selectedRevision + 1, receipt.ResultingRevision);
        return snapshots.ToArray();
    }

    private static async Task SeedIdentityAnchorGraphAsync(
        ServiceProvider provider,
        string tenantId,
        int anchorCount,
        int resolutionCount)
    {
        using IServiceScope seedScope = provider.CreateScope();
        StaffDbContext staff = seedScope.ServiceProvider
            .GetRequiredService<StaffDbContext>();
        WorkspacesDbContext workspaces = seedScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        await staff.Database.MigrateAsync().ConfigureAwait(false);
        await workspaces.Database.MigrateAsync().ConfigureAwait(false);

        StaffMember member = CreateMember(
            tenantId,
            $"Anchor target {tenantId[^1]}");
        staff.StaffMembers.Add(member);
        await staff.SaveChangesAsync().ConfigureAwait(false);

        await SeedIdentityAnchorRecordsAsync(
            staff, seedScope.ServiceProvider, tenantId, member.Id,
            anchorCount, resolutionCount).ConfigureAwait(false);
    }

    private static async Task SeedIdentityAnchorRecordsAsync(
        StaffDbContext staff,
        IServiceProvider services,
        string tenantId,
        Guid staffMemberId,
        int anchorCount,
        int resolutionCount)
    {
        Assert.InRange(resolutionCount, 0, anchorCount);
        long staffVersion = await staff.StaffMembers
            .Where(member => member.Id == staffMemberId)
            .Select(member => member.Version)
            .SingleAsync().ConfigureAwait(false);
        IStaffIdentityProvisioningAnchorRepository anchors = services
            .GetRequiredService<
                IStaffIdentityProvisioningAnchorRepository>();
        IStaffIdentityProvisioningAnchorResolutionRepository resolutions =
            services.GetRequiredService<
                IStaffIdentityProvisioningAnchorResolutionRepository>();
        IStaffOnboardingProvisioningOperationRepository operations = services
            .GetRequiredService<IStaffOnboardingProvisioningOperationRepository>();
        Guid[] sourceIds = Enumerable.Range(1, anchorCount)
            .Select(index => DeterministicSourceId(tenantId, index))
            .ToArray();
        Dictionary<Guid, Guid> resolutionIds = [];
        foreach (Guid sourceId in sourceIds)
        {
            Guid resolutionEventId = Guid.NewGuid();
            Assert.NotEqual(sourceId, resolutionEventId);
            resolutionIds.Add(sourceId, resolutionEventId);
            // Keep the source receipt and its exact anchor in the same commit.
            // These fixtures exercise export/deletion, not profile hashing.
            await operations.AddAsync(
                new StaffMemberMutationOperationRecord(
                    sourceId,
                    tenantId,
                    staffMemberId,
                    StaffMemberMutationKind.OnboardingProvision,
                    staffVersion,
                    Digest,
                    StaffStatus.Active,
                    staffVersion,
                    SeedNowUtc.AddMinutes(5)),
                CancellationToken.None).ConfigureAwait(false);
            await anchors.AddAsync(
                new StaffIdentityProvisioningAnchorRecord(
                    tenantId,
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                    sourceId,
                    staffMemberId,
                    SeedNowUtc.AddMinutes(5),
                    resolutionEventId),
                CancellationToken.None).ConfigureAwait(false);
        }

        await staff.SaveChangesAsync().ConfigureAwait(false);
        foreach (Guid sourceId in sourceIds.Take(resolutionCount))
        {
            await resolutions.AddAsync(
                new StaffIdentityProvisioningAnchorResolutionRecord(
                    tenantId,
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                    sourceId,
                    staffMemberId,
                    WorkspaceApplicationVersion: 1,
                    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                        .CompletedRedacted,
                    resolutionIds[sourceId],
                    SeedNowUtc.AddMinutes(6)),
                CancellationToken.None).ConfigureAwait(false);
        }

        await staff.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Guid DeterministicSourceId(
        string tenantId,
        int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.Parse(tenantId).TryWriteBytes(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            bytes,
            ordinal);
        return new Guid(bytes);
    }

    private static async Task<int?> ReadDestroyStageAsync(
        string connectionString,
        string tenantId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT "Stage"
            FROM staff.tenant_destroy_operations
            WHERE "ScopeId" = @tenantId;
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        object? value = await command.ExecuteScalarAsync()
            .ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToInt32(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DestroyReceiptSnapshot> ReadDestroyReceiptAsync(
        string connectionString,
        string tenantId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT
                "OperationId",
                "ScopeId",
                "SelectedRevision",
                "ResultingRevision"
            FROM staff.tenant_destroy_receipts
            WHERE "ScopeId" = @tenantId;
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync()
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));
        DestroyReceiptSnapshot snapshot = new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
        return snapshot;
    }

    private sealed record DestroyStageSnapshot(
        TenantTerminationContributionStatus Status,
        int? Stage,
        long ResolutionCount,
        long AnchorCount,
        long StaffMemberCount);

    private sealed record DestroyReceiptSnapshot(
        Guid OperationId,
        string ScopeId,
        long SelectedRevision,
        long ResultingRevision);

    private static async Task<(Guid StaffId, ProofIds ProofIds)>
        SeedGraphAsync(
            StaffDbContext context,
            IStaffMemberMutationOperationRepository mutationOperations,
            string tenantId)
    {
        StaffMember member = CreateMember(tenantId, "Maya Chen");
        _ = member.AssignProperty(
            Guid.NewGuid(),
            PropertyId,
            "Front desk",
            isPrimary: true,
            new DateOnly(2026, 1, 1),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            SeedNowUtc.AddMinutes(1)).Value;
        Assert.True(member.UnassignProperty(
            PropertyId,
            new DateOnly(2026, 7, 30),
            member.Version,
            "user:owner",
            "Moved to another property.",
            Guid.NewGuid(),
            SeedNowUtc.AddMinutes(2)).IsSuccess);
        context.StaffMembers.Add(member);
        await mutationOperations.AddAsync(
            new StaffMemberMutationOperationRecord(
                Guid.NewGuid(),
                tenantId,
                member.Id,
                StaffMemberMutationKind.ProfileUpdate,
                member.Version,
                Digest,
                StaffStatus.Active,
                member.Version,
                SeedNowUtc.AddMinutes(3)),
            CancellationToken.None);

        StaffDataRightsCorrectionReceipt correction =
            StaffDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                CaseId,
                approvalRevision: 1,
                member.Id,
                selectedRecordVersion: 2,
                currentRecordVersion: 3,
                [StaffProfileField.DisplayName],
                Digest,
                Guid.NewGuid(),
                Guid.NewGuid(),
                SeedNowUtc.AddMinutes(3)).Value;
        context.DataRightsCorrectionReceipts.Add(correction);

        StaffProcessingRestriction restriction =
            StaffProcessingRestriction.Create(
                Guid.NewGuid(),
                tenantId,
                member.Id,
                CaseId,
                applyApprovalRevision: 1,
                applySelectedStaffVersion: member.Version,
                "user:owner",
                SeedNowUtc.AddMinutes(4)).Value;
        StaffProcessingRestrictionReceipt restrictionReceipt =
            StaffProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                restriction.Id,
                StaffProcessingRestrictionAction.Apply,
                member.Id,
                CaseId,
                approvalRevision: 1,
                selectedStaffVersion: member.Version,
                StaffProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: restriction.Version,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:owner",
                Guid.NewGuid(),
                SeedNowUtc.AddMinutes(4)).Value;
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionReceipts.Add(restrictionReceipt);

        StaffEmploymentGovernance governance = CreateGovernance(
            tenantId,
            member,
            SeedNowUtc.AddMinutes(5));
        StaffEmploymentGovernanceChangeReceipt governanceReceipt =
            StaffEmploymentGovernanceChangeReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                governance,
                previousGovernanceVersion: 0,
                Digest,
                "user:privacy",
                SeedNowUtc.AddMinutes(5)).Value;
        context.EmploymentGovernance.Add(governance);
        context.EmploymentGovernanceChangeReceipts.Add(governanceReceipt);

        StaffDataHold hold = StaffDataHold.Place(
            Guid.NewGuid(),
            tenantId,
            member.Id,
            "regulatory-request",
            "user:owner",
            SeedNowUtc.AddMinutes(6)).Value;
        StaffDataHoldReceipt holdReceipt = StaffDataHoldReceipt.Create(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            hold,
            StaffDataHoldAction.Place,
            member.Version,
            "user:owner",
            SeedNowUtc.AddMinutes(6)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(holdReceipt);

        (StaffAnonymisationReceipt anonymisationReceipt,
            StaffAnonymisationTombstone tombstone,
            StaffAnonymisationRestoreReceipt restoreReceipt) =
            SeedAnonymisationProof(context, tenantId);
        StaffRetentionAnonymisationReceipt retentionReceipt =
            SeedRetentionProof(context, tenantId);

        await context.SaveChangesAsync();
        return (
            member.Id,
            new(
                correction.Id,
                restrictionReceipt.Id,
                governanceReceipt.Id,
                holdReceipt.Id,
                anonymisationReceipt.Id,
                restoreReceipt.Id,
                retentionReceipt.Id,
                tombstone.Id));
    }

    private static (
        StaffAnonymisationReceipt Receipt,
        StaffAnonymisationTombstone Tombstone,
        StaffAnonymisationRestoreReceipt RestoreReceipt)
        SeedAnonymisationProof(
            StaffDbContext context,
            string tenantId)
    {
        StaffMember member = CreateDepartedMember(
            tenantId,
            "Anonymised Staff Source",
            SeedNowUtc.AddDays(-2));
        StaffMemberAnonymisationOutcome outcome = member.Anonymise(
            member.Version,
            "user:privacy-executor",
            Guid.NewGuid(),
            SeedNowUtc.AddMinutes(7)).Value;
        StaffAnonymisationReceipt receipt =
            StaffAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                member.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                selectedOperationLockRevision: 1,
                resultingOperationLockRevision: 2,
                Digest,
                Digest,
                outcome.EventId,
                "user:privacy-executor",
                outcome.OccurredAtUtc).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.Create(
                tenantId,
                member.Id,
                receipt.CompletedAtUtc,
                receipt.CanonicalSha256).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = SeedNowUtc.AddMinutes(8);
        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            receipt.CompletedAtUtc,
            receipt.CanonicalSha256,
            replayedAtUtc).IsSuccess);
        StaffAnonymisationRestoreReceipt restoreReceipt =
            StaffAnonymisationRestoreReceipt.Create(
                tenantId,
                ledgerEntryId,
                member.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingStaffVersion,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.StaffMembers.Add(member);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
        return (receipt, tombstone, restoreReceipt);
    }

    private static StaffRetentionAnonymisationReceipt SeedRetentionProof(
        StaffDbContext context,
        string tenantId)
    {
        StaffMember member = CreateDepartedMember(
            tenantId,
            "Retention Staff",
            SeedNowUtc.AddDays(-5));
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                Guid.NewGuid(),
                tenantId,
                "staff-operational",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                SeedNowUtc.AddMinutes(9),
                SeedNowUtc.AddMinutes(30)).Value;
        StaffMemberAnonymisationOutcome outcome = member.Anonymise(
            member.Version,
            "system:retention",
            Guid.NewGuid(),
            SeedNowUtc.AddMinutes(10)).Value;
        StaffRetentionAnonymisationReceipt receipt =
            StaffRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                execution.Id,
                member.Id,
                outcome,
                selectedOperationLockRevision: 1,
                resultingOperationLockRevision: 2,
                member.DepartedAtUtc!.Value,
                SeedNowUtc.AddDays(-1),
                Digest).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.CreateForRetention(receipt).Value;

        context.StaffMembers.Add(member);
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
            "The workspace is not accepting Staff mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices)
    {
        using IServiceScope scope = rootServices.CreateScope();
        StaffDbContext context = scope.ServiceProvider
            .GetRequiredService<StaffDbContext>();
        context.StaffMembers.Add(CreateMember(TenantA, "Blocked Staff"));
        await context.SaveChangesAsync();
    }

    private static async Task AssertOwnerProofIsDatabaseProtectedAsync(
        IServiceProvider services,
        ProofIds ids)
    {
        StaffDbContext context = services.GetRequiredService<StaffDbContext>();
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            UPDATE staff.data_rights_correction_receipts
            SET "ApprovalRevision" = "ApprovalRevision"
            WHERE "Id" = {ids.CorrectionReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_processing_restriction_receipts
            WHERE "Id" = {ids.RestrictionReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_employment_governance_change_receipts
            WHERE "Id" = {ids.GovernanceReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_data_hold_receipts
            WHERE "Id" = {ids.HoldReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_anonymisation_receipts
            WHERE "Id" = {ids.AnonymisationReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_anonymisation_restore_receipts
            WHERE "Id" = {ids.RestoreReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM staff.staff_retention_anonymisation_receipts
            WHERE "Id" = {ids.RetentionReceiptId};
            """);

        PostgresException tombstoneDelete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM staff.staff_anonymisation_tombstones
                WHERE "Id" = {ids.TombstoneId};
                """));
        Assert.Equal("P0001", tombstoneDelete.SqlState);
        Assert.Contains(
            "Staff anonymisation tombstones cannot be deleted",
            tombstoneDelete.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task AssertReceiptTriggerRejectedAsync(
        StaffDbContext context,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains(
            "Staff receipts are append-only",
            failure.MessageText,
            StringComparison.Ordinal);
    }

    private static StaffMember CreateMember(
        string tenantId,
        string displayName) =>
        StaffMember.Create(
            Guid.NewGuid(),
            tenantId,
            displayName,
            $"{displayName} Legal",
            $"{displayName.Replace(' ', '.').ToLowerInvariant()}" +
            $".{Guid.NewGuid():N}@example.test",
            "+44 20 1234 5678",
            $"EMP-{Guid.NewGuid():N}"[..12],
            "Manager",
            "Operations",
            displayName == "Maya Chen" ? "account-maya" : null,
            "user:owner",
            Guid.NewGuid(),
            SeedNowUtc.AddDays(-10)).Value;

    private static StaffMember CreateDepartedMember(
        string tenantId,
        string displayName,
        DateTimeOffset departedAtUtc)
    {
        StaffMember member = CreateMember(tenantId, displayName);
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:owner",
            "Employment ended.",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        return member;
    }

    private static StaffEmploymentGovernance CreateGovernance(
        string tenantId,
        StaffMember member,
        DateTimeOffset configuredAtUtc) =>
        StaffEmploymentGovernance.Configure(
            tenantId,
            member.Id,
            member.Version,
            StaffEmploymentGovernanceBinding.Create(
                "GB",
                "staff-test",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "staff-employment",
                1,
                Digest,
                new DateTimeOffset(
                    2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(
                    2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                configuredAtUtc.AddMinutes(-1)).Value,
            [
                StaffEmploymentGovernanceAcknowledgement.Create(
                    "operator-notice",
                    1).Value
            ],
            "user:privacy",
            configuredAtUtc).Value;

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
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
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
                StaffTenantTerminationMetadata.OwnerKey,
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
        builder.Services.AddSingleton<IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddStaffPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed record ProofIds(
        Guid CorrectionReceiptId,
        Guid RestrictionReceiptId,
        Guid GovernanceReceiptId,
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
