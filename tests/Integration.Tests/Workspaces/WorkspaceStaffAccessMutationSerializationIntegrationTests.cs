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
    WorkspaceStaffAccessMutationSerializationIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 15, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Same_subject_waits_and_reloads_while_unrelated_coordinates_progress()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_staff_access_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        WorkspaceStaffAccessProcess seeded =
            await SeedAsync(connectionString).ConfigureAwait(false);

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

        await CreateCoordinator(firstDb).AcquireSubjectAsync(
                seeded.SubjectId,
                CancellationToken.None).ConfigureAwait(false);
        WorkspaceStaffAccessProcess first = await firstDb
            .StaffAccessProcesses.SingleAsync(process =>
                process.Id == seeded.Id).ConfigureAwait(false);
        Assert.True(first.RecordFailure("winner", Now).IsSuccess);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task<WorkspaceStaffAccessProcess?> waiting =
            CreateCoordinator(waitingDb).AcquireExistingAsync(
                seeded.Id,
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
            await CreateCoordinator(unrelatedDb).AcquireCoordinatesAsync(
                    Guid.NewGuid(),
                    "subject-b",
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        WorkspaceStaffAccessProcess resumed = Assert.IsType<
            WorkspaceStaffAccessProcess>(
            await waiting.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false));
        Assert.Equal("winner", resumed.FailureCode);
        Assert.Equal(2, resumed.Version);

        Assert.True(
            resumed.RecordFailure("waiter", Now.AddMinutes(1)).IsSuccess);
        await waitingDb.SaveChangesAsync().ConfigureAwait(false);
        await waitingTransaction.CommitAsync().ConfigureAwait(false);

        await using WorkspacesDbContext verification =
            CreateDbContext(connectionString);
        WorkspaceStaffAccessProcess persisted = await verification
            .StaffAccessProcesses.SingleAsync(process =>
                process.Id == seeded.Id)
            .ConfigureAwait(false);
        Assert.Equal("waiter", persisted.FailureCode);
        Assert.Equal(3, persisted.Version);
    }

    private static async Task<WorkspaceStaffAccessProcess> SeedAsync(
        string connectionString)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                "subject-a",
                WorkspaceStaffAccessTargetState.Suspended,
                targetStaffVersion: 2,
                new DateOnly(2026, 8, 6),
                "user:owner",
                [],
                Now).Value;
        dbContext.StaffAccessProcesses.Add(process);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return process;
    }

    private static WorkspaceStaffAccessMutationCoordinator CreateCoordinator(
        WorkspacesDbContext dbContext) => new(
        new WorkspaceStaffAccessOperationLock(dbContext),
        new WorkspaceStaffAccessProcessRepository(dbContext));

    private static WorkspacesDbContext CreateDbContext(
        string connectionString) => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql(
                connectionString,
                options => options.MigrationsAssembly(
                    WorkspacesMigrations.PostgreSqlAssembly))
            .Options,
        new TestScopeContext());

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
