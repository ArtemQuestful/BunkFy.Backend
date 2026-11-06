namespace BunkFy.Modules.Properties.Tests.Persistence;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Persistence;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyGovernanceRevisionPersistenceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 21, 14, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Constructor_normalizes_canonical_revision_evidence()
    {
        PropertyGovernanceRevision revision = new(CreateWriteModel(
            scopeId: $"  {TenantA}  ",
            decisionReasonCode: "  Allowed  ",
            actorId: "  user:owner  "));

        Assert.Equal(TenantA, revision.ScopeId);
        Assert.Equal("Allowed", revision.DecisionReasonCode);
        Assert.Equal("user:owner", revision.ActorId);
        Assert.Equal(TimeSpan.Zero, revision.OccurredAtUtc.Offset);
        Assert.Equal(OccurredAt.ToUniversalTime(), revision.OccurredAtUtc);
    }

    [Fact]
    public void Constructor_rejects_malformed_or_inconsistent_evidence()
    {
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                revisionId: Guid.Empty)));
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                scopeId: "tenant a")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                propertyVersion: 1)));
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                current: Coordinates(operatingCountryCode: "gb"))));
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                action: PropertyGovernanceRevisionAction.Rebound,
                previous: null)));
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel(
                action: PropertyGovernanceRevisionAction.Suspended,
                previous: Coordinates(policyId: "different-policy"))));
        Assert.Throws<ArgumentException>(() =>
            new PropertyGovernanceRevision(CreateWriteModel() with
            {
                OccurredAtUtc = default
            }));
    }

    [Fact]
    public async Task Query_filter_exposes_only_the_active_tenant()
    {
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();

        await using (PropertiesDbContext tenantA = CreateContext(
            databaseName,
            root,
            TenantA))
        {
            tenantA.GovernanceRevisions.Add(new(CreateWriteModel(
                revisionId: Guid.Parse(
                    "20000000-0000-0000-0000-000000000001"),
                scopeId: TenantA)));
            await tenantA.SaveChangesAsync();
        }

        await using (PropertiesDbContext tenantB = CreateContext(
            databaseName,
            root,
            TenantB))
        {
            tenantB.GovernanceRevisions.Add(new(CreateWriteModel(
                revisionId: Guid.Parse(
                    "20000000-0000-0000-0000-000000000002"),
                scopeId: TenantB)));
            await tenantB.SaveChangesAsync();
        }

        await using PropertiesDbContext reader = CreateContext(
            databaseName,
            root,
            TenantA);
        PropertyGovernanceRevision visible =
            await reader.GovernanceRevisions.SingleAsync();

        Assert.Equal(TenantA, visible.ScopeId);
        Assert.Equal(
            2,
            await reader.GovernanceRevisions
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Fact]
    public async Task Scoped_write_guard_rejects_a_foreign_revision()
    {
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();
        await using PropertiesDbContext context = CreateContext(
            databaseName,
            root,
            TenantA);
        context.GovernanceRevisions.Add(new(CreateWriteModel(
            scopeId: TenantB)));

        await Assert.ThrowsAsync<ScopeWriteGuardException>(
            () => context.SaveChangesAsync());

        context.ChangeTracker.Clear();
        Assert.Empty(await context.GovernanceRevisions
            .IgnoreQueryFilters()
            .ToListAsync());
    }

    private static PropertyGovernanceRevisionWriteModel CreateWriteModel(
        Guid? revisionId = null,
        string scopeId = TenantA,
        long propertyVersion = 2,
        PropertyGovernanceRevisionAction action =
            PropertyGovernanceRevisionAction.Activated,
        string decisionReasonCode = "Allowed",
        PropertyGovernanceRevisionCoordinates? previous = null,
        PropertyGovernanceRevisionCoordinates? current = null,
        string actorId = "user:owner",
        DateTimeOffset? occurredAtUtc = null) =>
        new(
            revisionId ?? Guid.Parse(
                "20000000-0000-0000-0000-000000000001"),
            scopeId,
            PropertyId,
            propertyVersion,
            action,
            decisionReasonCode,
            previous,
            current ?? Coordinates(),
            actorId,
            occurredAtUtc ?? OccurredAt);

    private static PropertyGovernanceRevisionCoordinates Coordinates(
        string operatingCountryCode = "GB",
        string policyId = "uk-hostel-policy") =>
        new(
            operatingCountryCode,
            policyId,
            PolicyVersion: 3,
            "eu-west",
            "standard-transfer",
            "hostel-retention",
            RetentionPolicyVersion: 2,
            Digest,
            Digest);

    private static PropertiesDbContext CreateContext(
        string databaseName,
        InMemoryDatabaseRoot root,
        string tenantId)
    {
        DbContextOptions<PropertiesDbContext> options =
            new DbContextOptionsBuilder<PropertiesDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class TestScopeContext(string tenantId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => tenantId;
    }
}
