namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryPersistenceRetryBehaviorTests
{
    [Theory]
    [InlineData(ConflictKind.OptimisticConcurrency)]
    [InlineData(ConflictKind.DbUpdateConcurrency)]
    [InlineData(ConflictKind.UniqueConstraint)]
    public async Task Block_group_persistence_conflict_reexecutes_once(
        ConflictKind conflictKind)
    {
        await using InventoryDbContext dbContext = CreateContext();
        dbContext.InventoryUnits.Add(InventoryUnit.CreateBed(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid()));
        InventoryPersistenceRetryBehavior<
            ReleaseManualInventoryBlockGroupCommand,
            ManualInventoryBlockGroupMutationReceiptDto> behavior = new(
                dbContext,
                _ => true);
        ReleaseManualInventoryBlockGroupCommand command = GroupCommand();
        int attempts = 0;

        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> Next()
        {
            attempts++;
            if (attempts == 1)
            {
                throw CreateConflict(conflictKind);
            }

            return Task.FromResult(Result.Failure<
                ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.ManagementOperationConflict));
        }

        Result<ManualInventoryBlockGroupMutationReceiptDto> result =
            await behavior.HandleAsync(
                command,
                Next,
                CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            result.Error);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Nonunique_database_failure_is_not_retried_or_cleared()
    {
        await using InventoryDbContext dbContext = CreateContext();
        dbContext.InventoryUnits.Add(InventoryUnit.CreateBed(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid()));
        InventoryPersistenceRetryBehavior<
            ReleaseManualInventoryBlockGroupCommand,
            ManualInventoryBlockGroupMutationReceiptDto> behavior = new(
                dbContext,
                _ => false);
        int attempts = 0;

        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> Next()
        {
            attempts++;
            throw new DbUpdateException("simulated nonunique failure");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => behavior.HandleAsync(
            GroupCommand(),
            Next,
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.NotEmpty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Unaffected_command_is_not_retried()
    {
        await using InventoryDbContext dbContext = CreateContext();
        InventoryPersistenceRetryBehavior<
            UnaffectedCommand,
            ManualInventoryBlockGroupMutationReceiptDto> behavior = new(
                dbContext,
                _ => true);
        int attempts = 0;

        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> Next()
        {
            attempts++;
            throw new DbUpdateConcurrencyException(
                "simulated concurrency conflict");
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            behavior.HandleAsync(
                new UnaffectedCommand(),
                Next,
                CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(ConflictKind.OptimisticConcurrency)]
    [InlineData(ConflictKind.DbUpdateConcurrency)]
    [InlineData(ConflictKind.UniqueConstraint)]
    public async Task Second_classified_failure_returns_version_conflict_without_a_retry_loop(
        ConflictKind conflictKind)
    {
        await using InventoryDbContext dbContext = CreateContext();
        InventoryPersistenceRetryBehavior<
            ReleaseManualInventoryBlockGroupCommand,
            ManualInventoryBlockGroupMutationReceiptDto> behavior = new(
                dbContext,
                _ => true);
        int attempts = 0;

        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> Next()
        {
            attempts++;
            if (attempts == 1)
            {
                throw CreateConflict(conflictKind);
            }

            dbContext.InventoryUnits.Add(InventoryUnit.CreateBed(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid()));
            throw CreateConflict(conflictKind);
        }

        Result<ManualInventoryBlockGroupMutationReceiptDto> result =
            await behavior.HandleAsync(
                GroupCommand(),
                Next,
                CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(
            InventoryApplicationErrors.VersionConflict,
            result.Error);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static Exception CreateConflict(ConflictKind conflictKind) =>
        conflictKind switch
        {
            ConflictKind.OptimisticConcurrency =>
                new OptimisticConcurrencyException(
                    "inventory",
                    new DbUpdateConcurrencyException(
                        "simulated concurrency conflict")),
            ConflictKind.DbUpdateConcurrency =>
                new DbUpdateConcurrencyException(
                    "simulated concurrency conflict"),
            ConflictKind.UniqueConstraint =>
                new DbUpdateException("simulated unique conflict"),
            _ => throw new ArgumentOutOfRangeException(nameof(conflictKind))
        };

    private static ReleaseManualInventoryBlockGroupCommand GroupCommand() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExpectedVersion: 1,
            Confirmed: true,
            ActorId: "user:test");

    private static InventoryDbContext CreateContext()
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new InventoryDbContext(options, new TestScopeContext());
    }

    public enum ConflictKind
    {
        OptimisticConcurrency = 1,
        DbUpdateConcurrency = 2,
        UniqueConstraint = 3
    }

    private sealed record UnaffectedCommand
        : ICommand<ManualInventoryBlockGroupMutationReceiptDto>;

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
