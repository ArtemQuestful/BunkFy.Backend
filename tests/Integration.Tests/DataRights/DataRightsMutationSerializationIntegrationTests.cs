namespace Integration.Tests;

using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.DataRights.Persistence.Repositories;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class DataRightsMutationSerializationIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 19, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Conflicting_graph_writers_wait_while_independent_work_progresses()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_data_rights_mutation_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        SeededState seeded = await SeedAsync(connectionString)
            .ConfigureAwait(false);

        await ProveCaseSerializationAsync(connectionString, seeded)
            .ConfigureAwait(false);
        await ProveProcessHierarchyAsync(connectionString, seeded.ProcessId)
            .ConfigureAwait(false);
    }

    private static async Task ProveCaseSerializationAsync(
        string connectionString,
        SeededState seeded)
    {
        await using DataRightsDbContext holderDb =
            CreateDbContext(connectionString);
        await using DataRightsDbContext waitingDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction holderTransaction =
            await holderDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        DataRightsCase holder = Assert.IsType<DataRightsCase>(
            await CreateCaseCoordinator(holderDb).AcquireAsync(
                DataRightsCaseScope.ForProperty(seeded.PropertyId),
                seeded.CaseId,
                CancellationToken.None).ConfigureAwait(false));
        Assert.True(holder.BeginDiscovery(
            holder.Version,
            "user:first",
            Now.AddMinutes(1)).IsSuccess);
        await holderDb.SaveChangesAsync().ConfigureAwait(false);

        Task<DataRightsCase?> waiting = CreateCaseCoordinator(waitingDb)
            .AcquireAsync(
                DataRightsCaseScope.ForProperty(seeded.PropertyId),
                seeded.CaseId,
                CancellationToken.None);
        await AssertStillWaitingAsync(waiting).ConfigureAwait(false);

        await using (DataRightsDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            DataRightsCase? unrelated = await CreateCaseCoordinator(unrelatedDb)
                .AcquireAsync(
                    DataRightsCaseScope.ForProperty(seeded.PropertyId),
                    seeded.UnrelatedCaseId,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.NotNull(unrelated);
            await unrelatedTransaction.RollbackAsync().ConfigureAwait(false);
        }

        await holderTransaction.CommitAsync().ConfigureAwait(false);
        DataRightsCase resumed = Assert.IsType<DataRightsCase>(
            await waiting.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false));
        Assert.Equal(DataRightsCaseState.Discovery, resumed.Status);
        Assert.Equal(2, resumed.Version);
        await waitingTransaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task ProveProcessHierarchyAsync(
        string connectionString,
        Guid processId)
    {
        Guid firstWorkItemId = Guid.NewGuid();
        Guid secondWorkItemId = Guid.NewGuid();
        await using DataRightsDbContext firstOwnerDb =
            CreateDbContext(connectionString);
        await using DataRightsDbContext secondOwnerDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction firstOwnerTransaction =
            await firstOwnerDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction secondOwnerTransaction =
            await secondOwnerDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        Assert.NotNull(await CreateTerminationCoordinator(firstOwnerDb)
            .AcquireOwnerWorkAsync(
                processId,
                firstWorkItemId,
                CancellationToken.None).ConfigureAwait(false));
        Assert.NotNull(await CreateTerminationCoordinator(secondOwnerDb)
            .AcquireOwnerWorkAsync(
                processId,
                secondWorkItemId,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false));

        await using DataRightsDbContext sameChildDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction sameChildTransaction =
            await sameChildDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        Task<TenantTerminationProcess?> sameChild =
            CreateTerminationCoordinator(sameChildDb)
                .AcquireOwnerWorkAsync(
                    processId,
                    firstWorkItemId,
                    CancellationToken.None);
        await AssertStillWaitingAsync(sameChild).ConfigureAwait(false);

        await using DataRightsDbContext exclusiveDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction exclusiveTransaction =
            await exclusiveDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        Task<TenantTerminationProcess?> exclusive =
            CreateTerminationCoordinator(exclusiveDb)
                .AcquireProcessAsync(processId, CancellationToken.None);
        await AssertStillWaitingAsync(exclusive).ConfigureAwait(false);

        await firstOwnerTransaction.RollbackAsync().ConfigureAwait(false);
        Assert.NotNull(await sameChild.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false));
        Assert.False(exclusive.IsCompleted);

        await secondOwnerTransaction.RollbackAsync().ConfigureAwait(false);
        Assert.False(exclusive.IsCompleted);
        await sameChildTransaction.RollbackAsync().ConfigureAwait(false);

        Assert.NotNull(await exclusive.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false));
        await exclusiveTransaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task<SeededState> SeedAsync(string connectionString)
    {
        await using DataRightsDbContext dbContext =
            CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateCase(propertyId);
        DataRightsCase unrelatedCase = CreateCase(propertyId);
        DataRightsCase terminationCase = CreateTenantTerminationCase();
        TenantTerminationProcess process = TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            terminationCase.Id,
            approvalRevision: 2,
            Guid.NewGuid(),
            exportRequested: false,
            new string('a', 64),
            "user:approver",
            Now,
            "system:tenant-termination",
            Now).Value;
        dbContext.Cases.AddRange(
            dataRightsCase,
            unrelatedCase,
            terminationCase);
        dbContext.TenantTerminationProcesses.Add(process);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return new(
            propertyId,
            dataRightsCase.Id,
            unrelatedCase.Id,
            process.Id);
    }

    private static DataRightsCase CreateCase(Guid propertyId)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        return DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy",
            Now).Value;
    }

    private static DataRightsCase CreateTenantTerminationCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:owner",
            Now).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: false,
            dataRightsCase.Version,
            "user:owner",
            Now).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCaseMutationCoordinator CreateCaseCoordinator(
        DataRightsDbContext dbContext)
    {
        DataRightsCaseRepository cases = new(dbContext);
        return new(
            cases,
            cases,
            cases,
            new DataRightsOperationLock(dbContext),
            new TestScopeContext());
    }

    private static TenantTerminationMutationCoordinator
        CreateTerminationCoordinator(DataRightsDbContext dbContext) => new(
        new TenantTerminationRepository(dbContext),
        new DataRightsCaseRepository(dbContext),
        new DataRightsOperationLock(dbContext),
        new TestScopeContext());

    private static async Task AssertStillWaitingAsync(Task operation)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(operation.IsCompleted);
    }

    private static DataRightsDbContext CreateDbContext(
        string connectionString) => new(
        new DbContextOptionsBuilder<DataRightsDbContext>()
            .UseNpgsql(
                connectionString,
                options => options.MigrationsAssembly(
                    DataRightsMigrations.PostgreSqlAssembly))
            .Options,
        new TestScopeContext());

    private sealed record SeededState(
        Guid PropertyId,
        Guid CaseId,
        Guid UnrelatedCaseId,
        Guid ProcessId);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;

        public string ScopeId => TenantId;
    }
}
