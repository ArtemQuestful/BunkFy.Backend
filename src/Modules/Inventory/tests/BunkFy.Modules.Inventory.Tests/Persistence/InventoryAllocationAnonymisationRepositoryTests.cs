namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    InventoryAllocationAnonymisationRepositoryTests
{
    private const string ScopeId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Owner_state_persists_with_decisions_removed()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryAllocation allocation = CreateRejected();
        dbContext.Allocations.Add(allocation);
        dbContext.AllocationAmendmentDecisions.Add(
            new(new InventoryAllocationAmendmentDecisionRecord(
                Guid.NewGuid(),
                ScopeId,
                allocation.Id,
                allocation.ReservationId,
                allocation.PropertyId,
                new string('d', 64),
                Confirmed: false,
                InventoryAllocationRejectionReason
                    .AllocationConflict,
                AllocationVersion: null,
                Now.AddMinutes(-1))));
        await dbContext.SaveChangesAsync();
        InventoryAllocationAnonymisationRepository repository =
            new(dbContext);
        Guid receiptId = Guid.NewGuid();
        InventoryAllocationAnonymisationOutcome outcome =
            allocation.Anonymise(
                allocation.Version,
                Guid.NewGuid(),
                Now).Value;
        int removed =
            await repository.RemoveAmendmentDecisionsAsync(
                allocation.PropertyId,
                allocation.Id,
                CancellationToken.None);
        InventoryAllocationAnonymisationReceipt receipt =
            InventoryAllocationAnonymisationReceipt.Create(
                receiptId,
                ScopeId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                allocation.PropertyId,
                Guid.NewGuid(),
                approvalRevision: 3,
                operationRevision: 4,
                allocation.Id,
                outcome,
                removed,
                new string('a', 64),
                "user:privacy-executor").Value;
        InventoryAllocationAnonymisationTombstone tombstone =
            InventoryAllocationAnonymisationTombstone.Create(
                receipt).Value;
        await repository.AddOwnerProofAsync(
            receipt,
            tombstone,
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        InventoryAllocationAnonymisationReceipt? persisted =
            await repository.FindReceiptByIdempotencyKeyAsync(
                receipt.IdempotencyKey,
                CancellationToken.None);
        bool proven = await repository.VerifyOwnerStateAsync(
            persisted!,
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.NotNull(persisted);
        Assert.Equal(receipt.CanonicalSha256,
            persisted.CanonicalSha256);
        Assert.True(proven);
        Assert.Empty(
            await dbContext.AllocationAmendmentDecisions
                .AsNoTracking()
                .ToArrayAsync());
    }

    [Fact]
    public async Task Removing_decisions_preserves_foreign_same_coordinates()
    {
        const string foreignScopeId = "tenant-b";
        InMemoryDatabaseRoot root = new();
        string databaseName = Guid.NewGuid().ToString("N");
        Guid amendmentRequestId = Guid.NewGuid();
        await using InventoryDbContext dbContext = CreateDbContext(
            ScopeId,
            databaseName,
            root);
        InventoryAllocation allocation = CreateRejected();
        dbContext.Allocations.Add(allocation);
        dbContext.AllocationAmendmentDecisions.Add(new(
            CreateDecision(
                ScopeId,
                amendmentRequestId,
                allocation,
                'a')));
        await dbContext.SaveChangesAsync();

        await using (InventoryDbContext foreign = CreateDbContext(
            foreignScopeId,
            databaseName,
            root))
        {
            foreign.AllocationAmendmentDecisions.Add(new(
                CreateDecision(
                    foreignScopeId,
                    amendmentRequestId,
                    allocation,
                    'b')));
            await foreign.SaveChangesAsync();
            foreign.ChangeTracker.Clear();

            InventoryAllocationAnonymisationRepository repository =
                new(dbContext);
            int removed = await repository.RemoveAmendmentDecisionsAsync(
                allocation.PropertyId,
                allocation.Id,
                CancellationToken.None);
            await dbContext.SaveChangesAsync();

            Assert.Equal(1, removed);
            Assert.Empty(await dbContext.AllocationAmendmentDecisions.ToArrayAsync());
            InventoryAllocationAmendmentDecision preserved = await foreign
                .AllocationAmendmentDecisions
                .SingleAsync();
            Assert.Equal(foreignScopeId, preserved.ScopeId);
            Assert.Equal(new string('b', 64), preserved.RequestFingerprint);
        }
    }

    [Fact]
    public async Task Operation_lock_is_singleton_and_monotonic()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryAllocationOperationLockRepository repository =
            new(dbContext);
        Guid allocationId = Guid.NewGuid();

        await repository.AcquireCoordinateAsync(
            ScopeId,
            allocationId,
            CancellationToken.None);
        await repository.AcquireCoordinateAsync(
            ScopeId,
            allocationId,
            CancellationToken.None);

        InventoryAllocationOperationLock resourceLock =
            Assert.Single(
                await dbContext.AllocationOperationLocks
                    .AsNoTracking()
                    .ToArrayAsync());
        Assert.Equal(allocationId, resourceLock.AllocationId);
        Assert.Equal(2, resourceLock.Revision);
    }

    [Fact]
    public async Task Existing_allocation_uses_preprovisioned_monotonic_lock()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryAllocation allocation = CreateRejected();
        await new InventoryAllocationRepository(dbContext).AddAsync(
            allocation,
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        InventoryAllocationOperationLockRepository repository =
            new(dbContext);

        Assert.True(await repository.TryAcquireExistingAsync(
            ScopeId,
            allocation.Id,
            CancellationToken.None));
        Assert.True(await repository.TryAcquireExistingAsync(
            ScopeId,
            allocation.Id,
            CancellationToken.None));

        InventoryAllocationOperationLock resourceLock = Assert.Single(
            await dbContext.AllocationOperationLocks
                .AsNoTracking()
                .ToArrayAsync());
        Assert.Equal(3, resourceLock.Revision);
    }

    [Fact]
    public async Task Existing_allocation_without_lock_fails_closed()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryAllocation allocation = CreateRejected();
        dbContext.Allocations.Add(allocation);
        await dbContext.SaveChangesAsync();
        InventoryAllocationOperationLockRepository repository =
            new(dbContext);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => repository.TryAcquireExistingAsync(
                ScopeId,
                allocation.Id,
                CancellationToken.None));

        Assert.Equal(
            "The inventory allocation operation lock is not provisioned.",
            exception.Message);
    }

    [Fact]
    public async Task Missing_allocation_does_not_create_an_operation_lock()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryAllocationOperationLockRepository repository =
            new(dbContext);

        bool acquired = await repository.TryAcquireExistingAsync(
            ScopeId,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(acquired);
        Assert.Empty(await dbContext.AllocationOperationLocks.ToArrayAsync());
    }

    private static InventoryAllocation CreateRejected() =>
        InventoryAllocation.CreateRejected(
            Guid.NewGuid(),
            ScopeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
            InventoryAllocationRejection.AllocationConflict,
            Now.AddDays(-1)).Value;

    private static InventoryDbContext CreateDbContext()
    {
        return CreateDbContext(
            ScopeId,
            $"inventory-anonymisation-{Guid.NewGuid():N}",
            new InMemoryDatabaseRoot());
    }

    private static InventoryDbContext CreateDbContext(
        string scopeId,
        string databaseName,
        InMemoryDatabaseRoot root)
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, new TestScopeContext(scopeId));
    }

    private static InventoryAllocationAmendmentDecisionRecord CreateDecision(
        string scopeId,
        Guid amendmentRequestId,
        InventoryAllocation allocation,
        char fingerprint) =>
        new(
            amendmentRequestId,
            scopeId,
            allocation.Id,
            allocation.ReservationId,
            allocation.PropertyId,
            new string(fingerprint, 64),
            Confirmed: false,
            InventoryAllocationRejectionReason.AllocationConflict,
            AllocationVersion: null,
            Now.AddMinutes(-1));

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
