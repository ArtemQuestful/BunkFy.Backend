namespace Integration.Tests;

using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffOnboardingSourceMutationSerializationIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 16, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Application_lock_preserves_the_committed_tuple_and_serializes_scoped_writers()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_onboarding_row_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        WorkspaceStaffOnboarding seeded =
            await SeedAsync(connectionString).ConfigureAwait(false);

        await using WorkspacesDbContext firstDb = CreateDbContext(connectionString);
        long committedTuple = await firstDb.Database.SqlQuery<long>($"""
                SELECT xmin::text::bigint AS "Value"
                FROM workspaces.staff_onboarding_applications
                WHERE "Id" = {seeded.Id} AND "ScopeId" = {TenantId}
                """).SingleAsync().ConfigureAwait(false);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        Assert.True(await new WorkspaceStaffOnboardingOperationLock(firstDb)
            .TryAcquireAsync(seeded.Id, CancellationToken.None).ConfigureAwait(false));
        long lockedTuple = await firstDb.Database.SqlQuery<long>($"""
                SELECT xmin::text::bigint AS "Value"
                FROM workspaces.staff_onboarding_applications
                WHERE "Id" = {seeded.Id} AND "ScopeId" = {TenantId}
                """).SingleAsync().ConfigureAwait(false);
        Assert.Equal(committedTuple, lockedTuple);

        await using (WorkspacesDbContext otherTenant = CreateDbContext(
                         connectionString, "tenant-b"))
        await using (IDbContextTransaction otherTransaction =
                     await otherTenant.Database.BeginTransactionAsync()
                         .ConfigureAwait(false))
        {
            Assert.False(await new WorkspaceStaffOnboardingOperationLock(otherTenant)
                .TryAcquireAsync(seeded.Id, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            await otherTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await using WorkspacesDbContext waitingDb = CreateDbContext(connectionString);
        waitingDb.Database.SetCommandTimeout(10);
        Task<int> waitingWriter = waitingDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workspaces.staff_onboarding_applications
            SET "Version" = "Version"
            WHERE "Id" = {seeded.Id} AND "ScopeId" = {TenantId}
            """);
        Task completed = await Task.WhenAny(waitingWriter,
            Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(false);
        Assert.NotSame(waitingWriter, completed);
        await firstTransaction.CommitAsync().ConfigureAwait(false);
        Assert.Equal(1, await waitingWriter.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Source_graph_waits_and_reloads_without_blocking_unrelated_coordinates()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_onboarding_source_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        WorkspaceStaffOnboarding seeded =
            await SeedAsync(connectionString).ConfigureAwait(false);

        await ProveApplicantIsolationAsync(
                connectionString,
                seeded.SourceId)
            .ConfigureAwait(false);
        await ProveSourceLifecycleIsolationAsync(
                connectionString,
                seeded)
            .ConfigureAwait(false);
    }

    private static async Task ProveApplicantIsolationAsync(
        string connectionString,
        Guid sourceId)
    {
        string subjectId = Guid.NewGuid().ToString("D");
        await using WorkspacesDbContext firstDb =
            CreateDbContext(connectionString);
        await using WorkspacesDbContext waitingDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        WorkspaceStaffOnboardingMutationLease first =
            await CreateCoordinator(firstDb).AcquireApplicantAsync(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                sourceId,
                subjectId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                CancellationToken.None).ConfigureAwait(false);
        Assert.False(first.CoordinateExists);

        Task<WorkspaceStaffOnboardingMutationLease> waiting =
            CreateCoordinator(waitingDb).AcquireApplicantAsync(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                sourceId,
                subjectId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(waiting.IsCompleted);

        await using (WorkspacesDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingMutationLease unrelated =
                await CreateCoordinator(unrelatedDb).AcquireApplicantAsync(
                        WorkspaceStaffOnboardingSource.EnrollmentLink,
                        sourceId,
                        Guid.NewGuid().ToString("D"),
                        WorkspaceStaffOnboardingSourceLockMode.Read,
                        requireOperational: false,
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            Assert.False(unrelated.CoordinateExists);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        WorkspaceStaffOnboardingMutationLease resumed =
            await waiting.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        Assert.False(resumed.CoordinateExists);
        await waitingTransaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task ProveSourceLifecycleIsolationAsync(
        string connectionString,
        WorkspaceStaffOnboarding seeded)
    {
        await using WorkspacesDbContext lifecycleDb =
            CreateDbContext(connectionString);
        await using WorkspacesDbContext waitingDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction lifecycleTransaction =
            await lifecycleDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        WorkspaceStaffOnboardingMutationCoordinator lifecycle =
            CreateCoordinator(lifecycleDb);
        await lifecycle.AcquireSourceAsync(
                seeded.SourceId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                CancellationToken.None).ConfigureAwait(false);
        WorkspaceStaffOnboarding current = await lifecycleDb
            .StaffOnboardingApplications.SingleAsync(application =>
                application.Id == seeded.Id)
            .ConfigureAwait(false);
        Assert.True(current.Expire(Now).IsSuccess);
        await lifecycleDb.SaveChangesAsync().ConfigureAwait(false);

        Task<WorkspaceStaffOnboardingMutationLease> waiting =
            CreateCoordinator(waitingDb).AcquireExistingAsync(
                seeded.Id,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(waiting.IsCompleted);

        await using (WorkspacesDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            await CreateCoordinator(unrelatedDb).AcquireSourceAsync(
                    Guid.NewGuid(),
                    WorkspaceStaffOnboardingSourceLockMode.Read,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await lifecycleTransaction.CommitAsync().ConfigureAwait(false);
        WorkspaceStaffOnboardingMutationLease resumed =
            await waiting.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        WorkspaceStaffOnboarding reloaded =
            Assert.IsType<WorkspaceStaffOnboarding>(resumed.Application);
        Assert.Equal(WorkspaceStaffOnboardingState.Expired, reloaded.Status);
        Assert.Equal(2, reloaded.Version);
        await waitingTransaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task<WorkspaceStaffOnboarding> SeedAsync(
        string connectionString)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                Guid.NewGuid(),
                Guid.NewGuid().ToString("D"),
                "applicant@example.test",
                "Applicant",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now.AddHours(-1)).Value;
        dbContext.StaffOnboardingApplications.Add(application);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return application;
    }

    private static WorkspaceStaffOnboardingMutationCoordinator
        CreateCoordinator(WorkspacesDbContext dbContext) => new(
            new WorkspaceStaffOnboardingOperationLock(dbContext),
            new WorkspaceStaffOnboardingRepository(dbContext));

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string tenantId = TenantId) => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql(
                connectionString,
                options => options.MigrationsAssembly(
                    WorkspacesMigrations.PostgreSqlAssembly))
            .Options,
        new TestScopeContext(tenantId));

    private sealed class TestScopeContext(string tenantId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => tenantId;
    }
}
