namespace Integration.Tests;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffOnboardingRetentionExecutionSerializationIntegrationTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string MigrationTenantId =
        "20000000-0000-0000-0000-000000000002";
    private const string PreviousMigration =
        "20260821184307_AddWorkspacePropertyAuthorityTenantIntegrity";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Same_execution_waits_and_reloads_while_another_execution_proceeds()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_retention_execution_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        Guid executionId = Guid.NewGuid();

        await ProveCompletedStageUpgradeAsync(connectionString)
            .ConfigureAwait(false);

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

        Result<WorkspaceStaffOnboardingRetentionExecutionStart> first =
            await CreateHandler(firstDb).HandleAsync(
                new(CreateRequest(executionId)),
                CancellationToken.None).ConfigureAwait(false);
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(first.Value.DispatchRequired);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task<Result<WorkspaceStaffOnboardingRetentionExecutionStart>> waiting =
            CreateHandler(waitingDb).HandleAsync(
                new(CreateRequest(executionId)),
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(waiting.IsCompleted);

        Guid unrelatedExecutionId = Guid.NewGuid();
        await using (WorkspacesDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            Result<WorkspaceStaffOnboardingRetentionExecutionStart> unrelated =
                await CreateHandler(unrelatedDb).HandleAsync(
                        new(CreateRequest(unrelatedExecutionId)),
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            Assert.True(unrelated.IsSuccess, unrelated.Error.Code);
            Assert.True(unrelated.Value.DispatchRequired);
            await unrelatedDb.SaveChangesAsync().ConfigureAwait(false);
            await unrelatedTransaction.CommitAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        Result<WorkspaceStaffOnboardingRetentionExecutionStart> resumed =
            await waiting.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);
        Assert.True(resumed.Value.DispatchRequired);
        Assert.Equal(0, resumed.Value.ScannedCount);
        Assert.Equal(0, resumed.Value.AffectedCount);
        await waitingTransaction.RollbackAsync().ConfigureAwait(false);

        await using WorkspacesDbContext verification =
            CreateDbContext(connectionString);
        Assert.Equal(
            2,
            await verification.StaffOnboardingRetentionExecutions.CountAsync()
                .ConfigureAwait(false));

        await Assert.ThrowsAsync<PostgresException>(() =>
            verification.GetService<IMigrator>().MigrateAsync(
                PreviousMigration,
                CancellationToken.None));
    }

    private static async Task ProveCompletedStageUpgradeAsync(
        string connectionString)
    {
        await using (WorkspacesDbContext previous =
            CreateDbContext(connectionString, MigrationTenantId))
        {
            await previous.GetService<IMigrator>().MigrateAsync(
                    PreviousMigration,
                    CancellationToken.None)
                .ConfigureAwait(false);
            WorkspaceTerminationFence fence =
                WorkspaceTerminationFence.Freeze(
                    Guid.NewGuid(),
                    MigrationTenantId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    approvalRevision: 1,
                    Guid.NewGuid(),
                    Digest,
                    "operator:migration-test",
                    StartedAtUtc).Value;
            WorkspaceTenantDestroyOperation operation = Assert.IsType<
                WorkspaceTenantDestroyOperation>(
                WorkspaceTenantDestroyOperation.TryCreate(
                    Guid.NewGuid(),
                    MigrationTenantId,
                    Digest,
                    fence.Id,
                    selectedFenceVersion: 1,
                    batchSize: 25,
                    StartedAtUtc));
            for (int stage = 1; stage < 20; stage++)
            {
                Assert.True(operation.AdvanceEmptyStage(
                    StartedAtUtc.AddMinutes(stage)));
            }

            Assert.Equal(20, (int)operation.Stage);
            previous.WorkspaceTerminationFences.Add(fence);
            previous.TenantDestroyOperations.Add(operation);
            await previous.SaveChangesAsync().ConfigureAwait(false);
        }

        await using WorkspacesDbContext upgraded =
            CreateDbContext(connectionString, MigrationTenantId);
        await upgraded.Database.MigrateAsync().ConfigureAwait(false);
        WorkspaceTenantDestroyOperation persisted =
            await upgraded.TenantDestroyOperations.SingleAsync()
                .ConfigureAwait(false);
        Assert.True(persisted.IsComplete);
        Assert.Equal(21, (int)persisted.Stage);
    }

    private static BeginWorkspaceStaffOnboardingRetentionExecutionCommandHandler
        CreateHandler(WorkspacesDbContext dbContext)
    {
        WorkspaceStaffOnboardingRetentionExecutionRepository repository =
            new(dbContext);
        WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator =
            new(
                new WorkspaceStaffOnboardingRetentionExecutionLock(dbContext),
                repository,
                new TestScopeContext(TenantId));
        return new(repository, coordinator, new TestScopeContext(TenantId));
    }

    private static RetentionContributionRequest CreateRequest(
        Guid executionId) => new(
        RetentionExecutionContract.CurrentVersion,
        executionId,
        TenantId,
        PropertyId: null,
        WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
        WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
        WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
        Attempt: 1,
        StartedAtUtc,
        StartedAtUtc.AddMinutes(10));

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
