namespace BunkFy.Modules.Inventory.Tests.Persistence;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryAllocationAmendmentDecisionRepositoryTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly DateTimeOffset DecidedAt =
        new(2026, 8, 21, 18, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Decision_normalizes_scope_and_time()
    {
        InventoryAllocationAmendmentDecision decision = new(
            Decision(" tenant-a ", Guid.NewGuid()));

        InventoryAllocationAmendmentDecisionRecord record = decision.ToRecord();

        Assert.Equal(TenantA, decision.ScopeId);
        Assert.Equal(TimeSpan.Zero, decision.DecidedAtUtc.Offset);
        Assert.Equal(decision.DecidedAtUtc, record.DecidedAtUtc);
    }

    [Fact]
    public void Decision_rejects_malformed_coordinates_and_outcomes()
    {
        InventoryAllocationAmendmentDecisionRecord valid =
            Decision(TenantA, Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new InventoryAllocationAmendmentDecision(
            valid with { AmendmentRequestId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new InventoryAllocationAmendmentDecision(
            valid with { RequestFingerprint = new string('A', 64) }));
        Assert.Throws<ArgumentException>(() => new InventoryAllocationAmendmentDecision(
            valid with { AllocationVersion = 1 }));
        Assert.Throws<ArgumentException>(() => new InventoryAllocationAmendmentDecision(
            valid with { DecidedAtUtc = default }));
    }

    [Fact]
    public async Task Repository_isolates_same_and_foreign_only_request_ids()
    {
        InMemoryDatabaseRoot root = new();
        string databaseName = Guid.NewGuid().ToString("N");
        Guid sharedId = Guid.NewGuid();
        Guid foreignOnlyId = Guid.NewGuid();

        await using (InventoryDbContext tenantA = CreateContext(
            TenantA,
            databaseName,
            root))
        {
            InventoryAllocationAmendmentDecisionRepository repository = new(tenantA);
            await repository.AddAsync(
                Decision(TenantA, sharedId, 'a'),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();
        }

        await using (InventoryDbContext tenantB = CreateContext(
            TenantB,
            databaseName,
            root))
        {
            InventoryAllocationAmendmentDecisionRepository repository = new(tenantB);
            await repository.AddAsync(
                Decision(TenantB, sharedId, 'b'),
                CancellationToken.None);
            await repository.AddAsync(
                Decision(TenantB, foreignOnlyId, 'c'),
                CancellationToken.None);
            await tenantB.SaveChangesAsync();
        }

        await using InventoryDbContext current = CreateContext(
            TenantA,
            databaseName,
            root);
        InventoryAllocationAmendmentDecisionRepository currentRepository =
            new(current);

        InventoryAllocationAmendmentDecisionRecord? local =
            await currentRepository.GetAsync(sharedId, CancellationToken.None);
        InventoryAllocationAmendmentDecisionRecord? foreignOnly =
            await currentRepository.GetAsync(foreignOnlyId, CancellationToken.None);

        Assert.NotNull(local);
        Assert.Equal(TenantA, local.ScopeId);
        Assert.Equal(new string('a', 64), local.RequestFingerprint);
        Assert.Null(foreignOnly);
        Assert.Equal(
            3,
            await current.AllocationAmendmentDecisions
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Fact]
    public async Task Repository_rejects_mismatched_or_unavailable_scope()
    {
        InMemoryDatabaseRoot root = new();
        string databaseName = Guid.NewGuid().ToString("N");
        await using InventoryDbContext current = CreateContext(
            TenantA,
            databaseName,
            root);
        InventoryAllocationAmendmentDecisionRepository repository = new(current);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(
            Decision(TenantB, Guid.NewGuid()),
            CancellationToken.None));

        await using InventoryDbContext disabled = CreateContext(
            TenantA,
            databaseName,
            root,
            enabled: false);
        InventoryAllocationAmendmentDecisionRepository disabledRepository =
            new(disabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabledRepository.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static InventoryAllocationAmendmentDecisionRecord Decision(
        string scopeId,
        Guid amendmentRequestId,
        char fingerprint = 'a') =>
        new(
            amendmentRequestId,
            scopeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string(fingerprint, 64),
            Confirmed: false,
            InventoryAllocationRejectionReason.AllocationConflict,
            AllocationVersion: null,
            DecidedAt);

    private static InventoryDbContext CreateContext(
        string scopeId,
        string databaseName,
        InMemoryDatabaseRoot root,
        bool enabled = true)
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, new TestScopeContext(scopeId, enabled));
    }

    private sealed class TestScopeContext(string scopeId, bool enabled)
        : IScopeContext
    {
        public bool IsEnabled { get; } = enabled;
        public string ScopeId { get; } = scopeId;
    }
}
