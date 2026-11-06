namespace Integration.Tests;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Retention.Persistence.Repositories;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class RetentionMutationSerializationIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 15, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Writers_wait_and_reload_the_committed_schedule_and_projection()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_retention_mutation_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");

        await SeedAsync(connectionString, propertyId).ConfigureAwait(false);
        await ProveScheduleSerializationAsync(connectionString, propertyId)
            .ConfigureAwait(false);
        await ProveProjectionSerializationAsync(connectionString, propertyId)
            .ConfigureAwait(false);
    }

    private static async Task ProveScheduleSerializationAsync(
        string connectionString,
        Guid propertyId)
    {
        Guid firstRunId = Guid.Parse(
            "20000000-0000-0000-0000-000000000001");
        Guid waitingRunId = Guid.Parse(
            "20000000-0000-0000-0000-000000000002");
        await using RetentionDbContext firstDb = CreateDbContext(connectionString);
        await using RetentionDbContext waitingDb = CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync().ConfigureAwait(false);

        Result<RetentionExecutionStart> first = await CreateBeginHandler(firstDb)
            .HandleAsync(
                CreateStartCommand(firstRunId, propertyId, Now),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(first.IsSuccess, first.Error.Code);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);
        Result<Unit> completed = await CreateCompleteHandler(firstDb)
            .HandleAsync(
                new(
                    firstRunId,
                    1,
                    new(
                        RetentionExecutionContract.CurrentVersion,
                        RetentionContributionStatus.Completed,
                        0,
                        0,
                        0,
                        "retention.integration.completed",
                        Now.AddSeconds(30))),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(completed.IsSuccess, completed.Error.Code);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task<Result<RetentionExecutionStart>> waiting = CreateBeginHandler(waitingDb)
            .HandleAsync(
                CreateStartCommand(
                    waitingRunId,
                    propertyId,
                    Now.AddMinutes(1)),
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waiting.IsCompleted);

        await using (RetentionDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            Result<RetentionExecutionStart> unrelated =
                await CreateBeginHandler(unrelatedDb)
                    .HandleAsync(
                        new(
                            Guid.NewGuid(),
                            TenantId,
                            "staff",
                            "staff-employment",
                            RetentionTargetScopeKind.Property,
                            propertyId,
                            1,
                            1,
                            Now,
                            Now.AddMinutes(5),
                            Now.AddHours(1)),
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            Assert.True(unrelated.IsSuccess, unrelated.Error.Code);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        Result<RetentionExecutionStart> resumed = await waiting
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);
        await waitingDb.SaveChangesAsync().ConfigureAwait(false);
        await waitingTransaction.CommitAsync().ConfigureAwait(false);

        await using RetentionDbContext verification =
            CreateDbContext(connectionString);
        RetentionScheduleState schedule = await verification.ScheduleStates
            .SingleAsync(state =>
                state.OwnerKey == "guests" &&
                state.DataClassKey == "guest-operational")
            .ConfigureAwait(false);
        Assert.Equal(waitingRunId, schedule.LastExecutionId);
        Assert.Equal(Now.AddMinutes(1), schedule.LastStartedAtUtc);
        Assert.Equal(3, schedule.Version);
    }

    private static async Task ProveProjectionSerializationAsync(
        string connectionString,
        Guid propertyId)
    {
        await using RetentionDbContext firstDb = CreateDbContext(connectionString);
        await using RetentionDbContext waitingDb = CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync().ConfigureAwait(false);

        await CreateScopeCoordinator(firstDb).ApplyPropertyTopologyAsync(
            new(TenantId, propertyId, false, 2),
            CancellationToken.None).ConfigureAwait(false);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task waiting = CreateScopeCoordinator(waitingDb).ApplyPropertyPolicyAsync(
            new(TenantId, propertyId, true, 2, 2),
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waiting.IsCompleted);

        await using (RetentionDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            await CreateScopeCoordinator(unrelatedDb).ApplyPropertyTopologyAsync(
                    new(TenantId, Guid.NewGuid(), true, 1),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await waitingDb.SaveChangesAsync().ConfigureAwait(false);
        await waitingTransaction.CommitAsync().ConfigureAwait(false);

        await using RetentionDbContext verification =
            CreateDbContext(connectionString);
        RetentionPropertyProjection projection = await verification
            .PropertyProjections.SingleAsync(property => property.Id == propertyId)
            .ConfigureAwait(false);
        Assert.False(projection.IsActive);
        Assert.Equal(2, projection.TopologySourceVersion);
        Assert.True(projection.IsProcessingEnabled);
        Assert.Equal(2, projection.RetentionPolicyVersion);
        Assert.Equal(2, projection.PolicySourceVersion);
    }

    private static async Task SeedAsync(
        string connectionString,
        Guid propertyId)
    {
        await using RetentionDbContext dbContext = CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        dbContext.TenantProjections.Add(
            new(TenantId, Guid.NewGuid(), true, 1));
        RetentionPropertyProjection property = new(
            TenantId,
            propertyId,
            true,
            1);
        property.ApplyPolicy(true, 1, 1);
        dbContext.PropertyProjections.Add(property);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static BeginRetentionExecutionCommandHandler CreateBeginHandler(
        RetentionDbContext dbContext)
    {
        RetentionExecutionRepository executions = new(dbContext);
        RetentionScopeRepository scopes = new(dbContext);
        RetentionScheduleStateRepository schedules = new(dbContext);
        RetentionExecutionMutationCoordinator mutations = new(
            new RetentionMutationLock(dbContext),
            executions,
            schedules,
            scopes,
            new TestScopeContext());
        return new(mutations, executions, schedules);
    }

    private static CompleteRetentionExecutionCommandHandler
        CreateCompleteHandler(RetentionDbContext dbContext)
    {
        RetentionExecutionRepository executions = new(dbContext);
        RetentionScheduleStateRepository schedules = new(dbContext);
        RetentionExecutionMutationCoordinator mutations = new(
            new RetentionMutationLock(dbContext),
            executions,
            schedules,
            new RetentionScopeRepository(dbContext),
            new TestScopeContext());
        return new(mutations, schedules);
    }

    private static RetentionScopeMutationCoordinator CreateScopeCoordinator(
        RetentionDbContext dbContext) => new(
        new RetentionMutationLock(dbContext),
        new RetentionScopeRepository(dbContext),
        new TestScopeContext());

    private static BeginRetentionExecutionCommand CreateStartCommand(
        Guid executionId,
        Guid propertyId,
        DateTimeOffset startedAtUtc) => new(
        executionId,
        TenantId,
        "guests",
        "guest-operational",
        RetentionTargetScopeKind.Property,
        propertyId,
        1,
        1,
        startedAtUtc,
        startedAtUtc.AddMinutes(5),
        startedAtUtc.AddHours(1));

    private static RetentionDbContext CreateDbContext(
        string connectionString) => new(
        new DbContextOptionsBuilder<RetentionDbContext>()
            .UseNpgsql(
                connectionString,
                options => options.MigrationsAssembly(
                    RetentionMigrations.PostgreSqlAssembly))
            .Options,
        new TestScopeContext(),
        OpenWorkspaceTerminationFenceReader.Instance);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
