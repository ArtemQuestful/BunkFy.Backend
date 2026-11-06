namespace BunkFy.Modules.Retention.Tests.Persistence;

using BunkFy.Modules.Retention.Persistence;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionScopeProjectionTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";

    [Fact]
    public void Tenant_projection_requires_valid_coordinates_and_version()
    {
        Assert.Throws<ArgumentException>(() => new RetentionTenantProjection(
            " ",
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1));
        Assert.Throws<ArgumentException>(() => new RetentionTenantProjection(
            TenantId,
            Guid.Empty,
            isActive: true,
            sourceVersion: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetentionTenantProjection(
                TenantId,
                Guid.NewGuid(),
                isActive: true,
                sourceVersion: 0));
    }

    [Fact]
    public void Tenant_projection_distinguishes_replay_stale_and_conflicting_facts()
    {
        Guid organizationId = Guid.NewGuid();
        RetentionTenantProjection projection = new(
            TenantId,
            organizationId,
            isActive: true,
            sourceVersion: 2);

        projection.Apply(organizationId, isActive: false, sourceVersion: 1);
        projection.Apply(organizationId, isActive: true, sourceVersion: 2);

        Assert.True(projection.IsActive);
        Assert.Equal(2, projection.SourceVersion);
        Assert.Throws<InvalidOperationException>(() => projection.Apply(
            organizationId,
            isActive: false,
            sourceVersion: 2));
        Assert.Throws<InvalidOperationException>(() => projection.Apply(
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1));

        projection.Apply(organizationId, isActive: false, sourceVersion: 3);

        Assert.False(projection.IsActive);
        Assert.Equal(3, projection.SourceVersion);
    }

    [Fact]
    public void Property_projection_requires_valid_placeholder_and_event_coordinates()
    {
        Assert.Throws<ArgumentException>(() => new RetentionPropertyProjection(
            TenantId,
            Guid.Empty,
            isActive: false,
            topologySourceVersion: 0));
        Assert.Throws<ArgumentException>(() => new RetentionPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            topologySourceVersion: 0));

        RetentionPropertyProjection projection = new(
            TenantId,
            Guid.NewGuid(),
            isActive: false,
            topologySourceVersion: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            projection.ApplyTopology(isActive: true, sourceVersion: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            projection.ApplyPolicy(
                isProcessingEnabled: true,
                retentionPolicyVersion: 0,
                sourceVersion: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            projection.ApplyPolicy(
                isProcessingEnabled: true,
                retentionPolicyVersion: 1,
                sourceVersion: 0));
    }

    [Fact]
    public void Property_projection_converges_independent_streams_and_rejects_conflicts()
    {
        RetentionPropertyProjection projection = new(
            TenantId,
            Guid.NewGuid(),
            isActive: false,
            topologySourceVersion: 0);

        projection.ApplyPolicy(
            isProcessingEnabled: true,
            retentionPolicyVersion: 4,
            sourceVersion: 2);

        Assert.True(projection.IsKnown);
        Assert.False(projection.IsActive);
        Assert.True(projection.IsProcessingEnabled);
        Assert.Equal(4, projection.RetentionPolicyVersion);
        Assert.False(projection.IsSchedulable);

        projection.ApplyPolicy(true, 4, 2);
        projection.ApplyPolicy(false, 3, 1);
        Assert.Throws<InvalidOperationException>(() =>
            projection.ApplyPolicy(false, 4, 2));
        Assert.Throws<InvalidOperationException>(() =>
            projection.ApplyPolicy(true, 5, 2));

        projection.ApplyTopology(isActive: true, sourceVersion: 3);
        projection.ApplyTopology(isActive: true, sourceVersion: 3);
        projection.ApplyTopology(isActive: false, sourceVersion: 2);

        Assert.True(projection.IsActive);
        Assert.True(projection.IsSchedulable);
        Assert.Equal(3, projection.TopologySourceVersion);
        Assert.Throws<InvalidOperationException>(() =>
            projection.ApplyTopology(isActive: false, sourceVersion: 3));
    }
}
