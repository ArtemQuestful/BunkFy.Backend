namespace Integration.Tests;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class WorkspacesPersistenceIntegrationTests
{
    private const string StaffAccessLifecycleMigration =
        "20260721174651_AddWorkspaceStaffAccessLifecycle";
    private const string ScopedStaffAccessSnapshotsMigration =
        "20260721203218_ScopeWorkspaceStaffAccessSnapshots";
    private const string StaffOnboardingCorrectionsMigration =
        "20260730104955_AddWorkspaceStaffOnboardingDataRightsCorrections";
    private const string WorkspaceStaffWithdrawalMigration =
        "20260809155756_AddWorkspaceStaffOnboardingWithdrawal";
    private const string WorkspaceStaffDeferredWithdrawalMigration =
        "20260811044039_AddWorkspaceStaffDeferredClaimWithdrawals";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Deferred_withdrawal_migration_upgrades_roundtrips_and_refuses_lossy_down()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
            "postgres:16-alpine")
            .WithDatabase("bunkfy_workspaces_deferred_migration_tests")
            .Build();
        await postgreSql.StartAsync();
        string tenantA = Guid.NewGuid().ToString("D");
        string tenantB = Guid.NewGuid().ToString("D");
        Guid organizationId = Guid.Parse(tenantA);
        Guid linkId = Guid.NewGuid();
        Guid claimId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset occurredAtUtc = new DateTimeOffset(
            2026,
            8,
            11,
            10,
            0,
            0,
            TimeSpan.Zero).AddTicks(1);

        await using (WorkspacesDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                WorkspaceStaffWithdrawalMigration);
        }

        await using (WorkspacesDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            await upgraded.Database.MigrateAsync();
            await upgraded.Database.MigrateAsync();
            string downScript = upgraded.Database.GetService<IMigrator>()
                .GenerateScript(
                    WorkspaceStaffDeferredWithdrawalMigration,
                    WorkspaceStaffWithdrawalMigration);
            int lockOrdinal = downScript.IndexOf(
                "LOCK TABLE workspaces.staff_deferred_claim_withdrawals",
                StringComparison.Ordinal);
            int dropOrdinal = downScript.IndexOf(
                "DROP TABLE workspaces.staff_deferred_claim_withdrawals",
                StringComparison.Ordinal);
            Assert.True(lockOrdinal >= 0);
            Assert.True(dropOrdinal > lockOrdinal);
            string[] indexes = await upgraded.Database.SqlQueryRaw<string>(
                    """
                    SELECT indexname AS "Value"
                    FROM pg_indexes
                    WHERE schemaname = 'workspaces'
                      AND tablename = 'staff_deferred_claim_withdrawals'
                    ORDER BY indexname
                    """)
                .ToArrayAsync();
            Assert.Contains(
                "IX_staff_deferred_claim_withdrawals_ScopeId_ClaimId",
                indexes);
            Assert.Contains(
                "IX_staff_deferred_claim_withdrawals_ScopeId_EnrollmentLinkId_C~",
                indexes);

            PostgresException mismatchedScope =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO workspaces.staff_deferred_claim_withdrawals (
                            "ClaimId", "OrganizationId", "EnrollmentLinkId",
                            "ClaimVersion", "EventId", "OccurredAtUtc", "ScopeId")
                        VALUES ({Guid.NewGuid()}, {organizationId}, {linkId},
                            {2L}, {Guid.NewGuid()}, {occurredAtUtc}, {tenantB})
                        """));
            Assert.Equal("23514", mismatchedScope.SqlState);
            PostgresException emptyCoordinate =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO workspaces.staff_deferred_claim_withdrawals (
                            "ClaimId", "OrganizationId", "EnrollmentLinkId",
                            "ClaimVersion", "EventId", "OccurredAtUtc", "ScopeId")
                        VALUES ({Guid.NewGuid()}, {organizationId}, {linkId},
                            {2L}, {Guid.Empty}, {occurredAtUtc}, {tenantA})
                        """));
            Assert.Equal("23514", emptyCoordinate.SqlState);

            upgraded.StaffDeferredClaimWithdrawals.Add(
                WorkspaceStaffDeferredClaimWithdrawal.Create(
                    organizationId.ToString("N").ToUpperInvariant(),
                    organizationId,
                    linkId,
                    claimId,
                    2,
                    eventId,
                    occurredAtUtc).Value);
            await upgraded.SaveChangesAsync();
        }

        await using (WorkspacesDbContext tenantAContext = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            WorkspaceStaffDeferredClaimWithdrawal roundTripped =
                await tenantAContext.StaffDeferredClaimWithdrawals.SingleAsync();
            Assert.Equal(tenantA, roundTripped.ScopeId);
            Assert.Equal(occurredAtUtc.AddTicks(-1), roundTripped.OccurredAtUtc);
            Assert.True(roundTripped.Matches(
                organizationId.ToString("N").ToUpperInvariant(),
                organizationId,
                linkId,
                claimId,
                2,
                eventId,
                occurredAtUtc));
        }

        await using (WorkspacesDbContext tenantBContext = CreateDbContext(
            postgreSql.GetConnectionString(), tenantB))
        {
            Assert.Empty(await tenantBContext.StaffDeferredClaimWithdrawals
                .ToArrayAsync());
            Assert.Single(await tenantBContext.StaffDeferredClaimWithdrawals
                .IgnoreQueryFilters()
                .ToArrayAsync());
        }

        await using (WorkspacesDbContext refusedDown = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            PostgresException refusal = await Assert.ThrowsAsync<PostgresException>(
                () => refusedDown.Database.GetService<IMigrator>().MigrateAsync(
                    WorkspaceStaffWithdrawalMigration));
            Assert.Equal("P0001", refusal.SqlState);
            Assert.Contains(
                "Cannot remove durable Staff claim withdrawals",
                refusal.MessageText,
                StringComparison.Ordinal);
        }

        await using (WorkspacesDbContext afterRefusal = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            Assert.Equal(
                claimId,
                (await afterRefusal.StaffDeferredClaimWithdrawals.SingleAsync()).Id);
            Assert.Equal(
                1,
                await afterRefusal.Database.SqlQueryRaw<int>(
                        """
                        SELECT COUNT(*)::int AS "Value"
                        FROM workspaces.__ef_migrations_history
                        WHERE "MigrationId" =
                            '20260811044039_AddWorkspaceStaffDeferredClaimWithdrawals'
                        """)
                    .SingleAsync());
        }

        await using (WorkspacesDbContext retained = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA))
        {
            retained.StaffDeferredClaimWithdrawals.Remove(
                await retained.StaffDeferredClaimWithdrawals.SingleAsync());
            await retained.SaveChangesAsync();
            await retained.Database.GetService<IMigrator>().MigrateAsync(
                WorkspaceStaffWithdrawalMigration);
            await retained.Database.MigrateAsync();
        }

        await using WorkspacesDbContext lockContext = CreateDbContext(
            postgreSql.GetConnectionString(), tenantA);
        await using var lockTransaction = await lockContext.Database
            .BeginTransactionAsync();
        await lockContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE workspaces.staff_deferred_claim_withdrawals " +
            "IN ACCESS EXCLUSIVE MODE");
        TaskCompletionSource<int> insertBackend = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> concurrentInsert = Task.Run(async () =>
        {
            await using NpgsqlConnection connection = new(
                postgreSql.GetConnectionString());
            await connection.OpenAsync();
            await using (NpgsqlCommand backend = new(
                "SELECT pg_backend_pid()",
                connection))
            {
                insertBackend.SetResult((int)(await backend.ExecuteScalarAsync())!);
            }

            await using NpgsqlCommand insert = new(
                """
                INSERT INTO workspaces.staff_deferred_claim_withdrawals (
                    "ClaimId", "OrganizationId", "EnrollmentLinkId",
                    "ClaimVersion", "EventId", "OccurredAtUtc", "ScopeId")
                VALUES (@claimId, @organizationId, @linkId, 2, @eventId,
                    @occurredAtUtc, @scopeId)
                """,
                connection);
            insert.Parameters.AddWithValue("claimId", Guid.NewGuid());
            insert.Parameters.AddWithValue("organizationId", organizationId);
            insert.Parameters.AddWithValue("linkId", linkId);
            insert.Parameters.AddWithValue("eventId", Guid.NewGuid());
            insert.Parameters.AddWithValue("occurredAtUtc", occurredAtUtc);
            insert.Parameters.AddWithValue("scopeId", tenantA);
            return await insert.ExecuteNonQueryAsync();
        });
        int backendPid = await insertBackend.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool insertIsWaiting = false;
        for (int attempt = 0; attempt < 100 && !insertIsWaiting; attempt++)
        {
            insertIsWaiting = await lockContext.Database.SqlQueryRaw<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity " +
                    "WHERE pid = {0} AND wait_event_type = 'Lock') AS \"Value\"",
                    backendPid)
                .SingleAsync();
            if (!insertIsWaiting)
            {
                await Task.Delay(25);
            }
        }

        if (!insertIsWaiting)
        {
            await lockTransaction.RollbackAsync();
            await concurrentInsert.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail(
                "The concurrent deferred insert never reached the migration table lock.");
        }

        await lockContext.Database.ExecuteSqlRawAsync(
            "DROP TABLE workspaces.staff_deferred_claim_withdrawals");
        await lockTransaction.CommitAsync();
        PostgresException insertFailure = await Assert.ThrowsAsync<PostgresException>(
            async () => { await concurrentInsert; });
        Assert.True(
            insertFailure.SqlState is "42P01" or "XX000",
            $"Unexpected concurrent-insert SQLSTATE: {insertFailure.SqlState}");
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Staff_onboarding_migration_enforces_scope_uniqueness_and_concurrency()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_workspaces_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid sourceId = Guid.NewGuid();
        string subjectId = Guid.NewGuid().ToString("D");
        WorkspaceStaffOnboarding tenantA = CreateApplication(TenantA, sourceId, subjectId);
        WorkspaceStaffOnboarding tenantB = CreateApplication(TenantB, sourceId, subjectId);

        await using (WorkspacesDbContext first = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await first.Database.MigrateAsync();
            first.StaffOnboardingApplications.Add(tenantA);
            await first.SaveChangesAsync();
        }

        await using (WorkspacesDbContext second = CreateDbContext(
            postgreSql.GetConnectionString(), TenantB))
        {
            second.StaffOnboardingApplications.Add(tenantB);
            await second.SaveChangesAsync();
            Assert.Single(await second.StaffOnboardingApplications.ToArrayAsync());
        }

        await using (WorkspacesDbContext scoped = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            WorkspaceStaffOnboarding visible = await scoped.StaffOnboardingApplications.SingleAsync();
            Assert.Equal(tenantA.Id, visible.Id);

            scoped.StaffOnboardingApplications.Add(CreateApplication(TenantA, sourceId, subjectId));
            await Assert.ThrowsAsync<DbUpdateException>(() => scoped.SaveChangesAsync());
        }

        await using WorkspacesDbContext current = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA);
        await using WorkspacesDbContext stale = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA);
        WorkspaceStaffOnboarding currentApplication = await current.StaffOnboardingApplications.SingleAsync();
        WorkspaceStaffOnboarding staleApplication = await stale.StaffOnboardingApplications.SingleAsync();

        Assert.True(currentApplication.UpdateSubmission(
            "verified@example.test", "Current profile", null, null, null, null, null, null,
            DateTimeOffset.UtcNow).IsSuccess);
        await current.SaveChangesAsync();
        Assert.True(staleApplication.UpdateSubmission(
            "verified@example.test", "Stale profile", null, null, null, null, null, null,
            DateTimeOffset.UtcNow.AddSeconds(1)).IsSuccess);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Staff_access_snapshot_migration_backfills_workspace_scope_and_allows_scoped_duplicates()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_workspaces_access_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid processId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid staffMemberId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid profileId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        DateTimeOffset changedAtUtc = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        await using (WorkspacesDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(StaffAccessLifecycleMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workspaces.staff_access_processes (
                    "Id", "StaffMemberId", "SubjectId", "TargetState", "TargetStaffVersion",
                    "EffectiveOn", "RequestedBy", "State", "Version", "CreatedAtUtc",
                    "LastChangedAtUtc", "CompletedAtUtc", "ScopeId")
                VALUES (
                    {processId}, {staffMemberId}, {"member-a"}, {2}, {2L},
                    {new DateOnly(2026, 7, 21)}, {"user:owner"}, {4}, {1L}, {changedAtUtc},
                    {changedAtUtc}, {changedAtUtc}, {TenantA});

                INSERT INTO workspaces.staff_access_profile_snapshots ("ProfileId", "ProcessId")
                VALUES ({profileId}, {processId});
                """);
        }

        await using (WorkspacesDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await upgraded.Database.MigrateAsync();
            WorkspaceStaffAccessProcess process = await upgraded.StaffAccessProcesses
                .Include(item => item.ProfileSnapshots)
                .SingleAsync(item => item.Id == processId);
            WorkspaceStaffAccessProfileSnapshot snapshot = Assert.Single(process.ProfileSnapshots);
            Assert.Equal(profileId, snapshot.ProfileId);
            Assert.Equal("tenant:tenant-a", snapshot.AssignmentScope);

            await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workspaces.staff_access_profile_snapshots (
                    "ProfileId", "ProcessId", "AssignmentScope")
                VALUES ({profileId}, {processId}, {"tenant:tenant-a/property:property-a"});
                """);
        }

        await using WorkspacesDbContext verified = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA);
        WorkspaceStaffAccessProcess reloaded = await verified.StaffAccessProcesses
            .Include(item => item.ProfileSnapshots)
            .SingleAsync(item => item.Id == processId);
        Assert.Equal(
            ["tenant:tenant-a", "tenant:tenant-a/property:property-a"],
            reloaded.ProfileSnapshots
                .Select(snapshot => snapshot.AssignmentScope)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Staff_access_plan_migration_preserves_existing_data_and_adds_scoped_plan_storage()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_workspaces_plan_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid sourceId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        string subjectId = Guid.NewGuid().ToString("D");
        WorkspaceStaffOnboarding existing = CreateApplication(TenantA, sourceId, subjectId);

        await using (WorkspacesDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                ScopedStaffAccessSnapshotsMigration);
            await SeedLegacyApplicationAsync(previous, existing);
        }

        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            TenantA,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            profileId,
            "front-desk",
            [propertyId],
            "owner-a",
            DateTimeOffset.UtcNow).Value;
        Assert.True(plan.Activate(DateTimeOffset.UtcNow.AddSeconds(1)).IsSuccess);

        await using (WorkspacesDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await upgraded.Database.MigrateAsync();
            Assert.Equal(
                existing.Id,
                (await upgraded.StaffOnboardingApplications.SingleAsync()).Id);

            upgraded.StaffAccessPlans.Add(plan);
            upgraded.PropertyProjections.Add(new WorkspacePropertyProjection(
                TenantA,
                propertyId,
                "Main House",
                PropertyStatus.Active,
                1));
            await upgraded.SaveChangesAsync();
        }

        await using WorkspacesDbContext verified = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA);
        WorkspaceStaffAccessPlan reloaded = await verified.StaffAccessPlans
            .Include(item => item.Properties)
            .SingleAsync(item => item.Id == sourceId);
        WorkspacePropertyProjection property = await verified.PropertyProjections
            .SingleAsync(item => item.Id == propertyId);

        Assert.Equal(WorkspaceStaffAccessPlanState.Active, reloaded.Status);
        Assert.Equal(propertyId, Assert.Single(reloaded.Properties).PropertyId);
        Assert.Equal("Main House", property.Name);
        Assert.Equal(PropertyStatus.Active, property.Status);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Staff_onboarding_restriction_migration_backfills_and_enforces_processing_state()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_restriction_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid sourceId = Guid.NewGuid();
        string subjectId = Guid.NewGuid().ToString("D");
        WorkspaceStaffOnboarding application =
            CreateApplication(TenantA, sourceId, subjectId);

        await using (WorkspacesDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                StaffOnboardingCorrectionsMigration);
            await SeedLegacyApplicationAsync(previous, application);
        }

        Guid restrictionId = Guid.NewGuid();
        DateTimeOffset appliedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (WorkspacesDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            await upgraded.Database.MigrateAsync();

            WorkspaceStaffOnboarding persistedApplication =
                await upgraded.StaffOnboardingApplications.SingleAsync();
            WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
                await upgraded
                    .StaffOnboardingProcessingRestrictionProjections
                    .SingleAsync();
            Assert.Equal(persistedApplication.Id, projection.ApplicationId);
            Assert.Equal(
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                projection.ContractVersion);
            Assert.Equal(0, projection.Revision);
            Assert.Equal(0, projection.ActiveRestrictionCount);
            Assert.False(projection.IsRestricted);
            Assert.True(projection.ProjectionOrdinal > 0);

            WorkspaceStaffOnboardingProcessingRestriction restriction =
                WorkspaceStaffOnboardingProcessingRestriction.Create(
                    restrictionId,
                    TenantA,
                    persistedApplication.Id,
                    Guid.NewGuid(),
                    1,
                    persistedApplication.Version,
                    "user:privacy-reviewer",
                    appliedAtUtc).Value;
            Assert.True(projection.Apply(
                projection.Revision,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                appliedAtUtc).IsSuccess);
            WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt =
                WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                    Guid.NewGuid(),
                    TenantA,
                    Guid.NewGuid(),
                    restriction.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                    persistedApplication.Id,
                    restriction.ApplyCaseId,
                    restriction.ApplyApprovalRevision,
                    restriction.ApplySelectedOnboardingVersion,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    restriction.Version,
                    projection.Revision,
                    projection.IsRestricted,
                    "user:privacy-reviewer",
                    Guid.NewGuid(),
                    appliedAtUtc).Value;
            upgraded.StaffOnboardingProcessingRestrictions.Add(restriction);
            upgraded.StaffOnboardingProcessingRestrictionReceipts.Add(receipt);
            await upgraded.SaveChangesAsync();
        }

        await using ServiceProvider provider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(), TenantA);
        await AssertRepositoryVisibilityAsync(
            provider,
            application.Id,
            sourceId,
            subjectId,
            expectedOperational: false);

        DateTimeOffset releasedAtUtc = appliedAtUtc.AddMinutes(1);
        await using (WorkspacesDbContext release = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA))
        {
            WorkspaceStaffOnboardingProcessingRestriction restriction =
                await release.StaffOnboardingProcessingRestrictions
                    .SingleAsync(item => item.Id == restrictionId);
            WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
                await release
                    .StaffOnboardingProcessingRestrictionProjections
                    .SingleAsync(item => item.ApplicationId == application.Id);
            Guid releaseCaseId = Guid.NewGuid();
            Assert.True(restriction.Release(
                releaseCaseId,
                2,
                application.Version,
                restriction.Version,
                "user:privacy-reviewer",
                releasedAtUtc).IsSuccess);
            Assert.True(projection.Release(
                projection.Revision,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                releasedAtUtc).IsSuccess);
            WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt =
                WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                    Guid.NewGuid(),
                    TenantA,
                    Guid.NewGuid(),
                    restriction.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionAction
                        .Release,
                    application.Id,
                    releaseCaseId,
                    2,
                    application.Version,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    restriction.Version,
                    projection.Revision,
                    projection.IsRestricted,
                    "user:privacy-reviewer",
                    Guid.NewGuid(),
                    releasedAtUtc).Value;
            release.StaffOnboardingProcessingRestrictionReceipts.Add(receipt);
            await release.SaveChangesAsync();
        }

        await AssertRepositoryVisibilityAsync(
            provider,
            application.Id,
            sourceId,
            subjectId,
            expectedOperational: true);

        await using WorkspacesDbContext tamper = CreateDbContext(
            postgreSql.GetConnectionString(), TenantA);
        PostgresException failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => tamper.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE workspaces.staff_onboarding_processing_restriction_receipts
                    SET "ActorId" = 'tampered'
                    """));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains("append-only", failure.MessageText);
    }

    private static async Task AssertRepositoryVisibilityAsync(
        ServiceProvider provider,
        Guid applicationId,
        Guid sourceId,
        string subjectId,
        bool expectedOperational)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IWorkspaceStaffOnboardingRepository repository =
            scope.ServiceProvider
                .GetRequiredService<IWorkspaceStaffOnboardingRepository>();

        Assert.NotNull(await repository.GetAsync(
            applicationId,
            CancellationToken.None));
        Assert.Equal(
            expectedOperational,
            await repository.GetOperationalAsync(
                applicationId,
                CancellationToken.None) is not null);
        Assert.Equal(
            applicationId,
            await repository.FindIdBySourceAndSubjectAsync(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                sourceId,
                subjectId,
                CancellationToken.None));
    }

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.AddWorkspacesPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private static WorkspacesDbContext CreateDbContext(string connectionString, string scopeId)
    {
        DbContextOptions<WorkspacesDbContext> options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql(
                connectionString,
                postgreSql => postgreSql
                    .MigrationsAssembly(WorkspacesMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(WorkspacesMigrations.HistoryTable, WorkspacesMigrations.Schema))
            .Options;
        return new WorkspacesDbContext(options, new TestScopeContext(scopeId));
    }

    private static Task<int> SeedLegacyApplicationAsync(
        WorkspacesDbContext dbContext,
        WorkspaceStaffOnboarding application) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "ClaimId", "ClaimVersion",
                "SubjectId", "VerifiedAccountEmail", "DisplayName", "LegalName",
                "WorkEmail", "WorkPhone", "EmployeeNumber", "JobTitle",
                "Department", "Status", "StaffMemberId", "FailureCode",
                "Version", "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES (
                {application.Id}, {(int)application.SourceKind},
                {application.SourceId}, {application.ClaimId},
                {application.ClaimVersion}, {application.SubjectId},
                {application.VerifiedAccountEmail}, {application.DisplayName},
                {application.LegalName}, {application.WorkEmail},
                {application.WorkPhone}, {application.EmployeeNumber},
                {application.JobTitle}, {application.Department},
                {(int)application.Status}, {application.StaffMemberId},
                {application.FailureCode}, {application.Version},
                {application.CreatedAtUtc}, {application.LastChangedAtUtc},
                {application.ScopeId});
            """);

    private static WorkspaceStaffOnboarding CreateApplication(
        string scopeId,
        Guid sourceId,
        string subjectId) => WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            sourceId,
            subjectId,
            "verified@example.test",
            "Ada Operator",
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow).Value;

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
