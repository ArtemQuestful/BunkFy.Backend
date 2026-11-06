namespace BunkFy.Modules.DataRights.Tests.Persistence;

using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Properties.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsPropertyProjectionIntegrityTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";

    [Fact]
    public void Projection_requires_valid_scoped_placeholder_coordinates()
    {
        Assert.Throws<ArgumentException>(() => new DataRightsPropertyProjection(
            " ",
            Guid.NewGuid(),
            null,
            null,
            PropertyStatus.Unknown,
            0));
        Assert.Throws<ArgumentException>(() => new DataRightsPropertyProjection(
            TenantId,
            Guid.Empty,
            null,
            null,
            PropertyStatus.Unknown,
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DataRightsPropertyProjection(
                TenantId,
                Guid.NewGuid(),
                null,
                null,
                PropertyStatus.Unknown,
                -1));
        Assert.Throws<ArgumentException>(() => new DataRightsPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            "Hostel",
            "UTC",
            PropertyStatus.Active,
            0));
        Assert.Throws<ArgumentException>(() => new DataRightsPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            " ",
            "UTC",
            PropertyStatus.Active,
            1));
    }

    [Fact]
    public void Topology_distinguishes_stale_replay_and_equal_version_conflict()
    {
        DataRightsPropertyProjection projection = CreatePlaceholder();

        projection.ApplyTopology(
            " Hostel ",
            " Europe/London ",
            PropertyStatus.Active,
            3);
        projection.ApplyTopology(
            "Hostel",
            "Europe/London",
            PropertyStatus.Active,
            3);
        projection.ApplyTopology(
            "Stale",
            "UTC",
            PropertyStatus.Retired,
            2);

        Assert.True(projection.IsKnown);
        Assert.Equal("Hostel", projection.Name);
        Assert.Equal("Europe/London", projection.TimeZoneId);
        Assert.Equal(PropertyStatus.Active, projection.Status);
        Assert.Equal(3, projection.TopologySourceVersion);
        Assert.Throws<InvalidOperationException>(() => projection.ApplyTopology(
            "Another hostel",
            "Europe/London",
            PropertyStatus.Active,
            3));
        Assert.Throws<InvalidOperationException>(() => projection.ApplyTopology(
            null,
            null,
            PropertyStatus.Retired,
            3));

        projection.ApplyTopology(
            null,
            null,
            PropertyStatus.Retired,
            4);

        Assert.Equal("Hostel", projection.Name);
        Assert.Equal("Europe/London", projection.TimeZoneId);
        Assert.Equal(PropertyStatus.Retired, projection.Status);
        Assert.Equal(4, projection.TopologySourceVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ApplyTopology(
            "Hostel",
            "UTC",
            PropertyStatus.Active,
            0));
    }

    [Fact]
    public void Policy_replay_is_order_independent_and_conflicts_fail_closed()
    {
        DataRightsPropertyProjection projection = CreatePlaceholder();
        PropertyGovernancePolicyBinding policy = CreatePolicy(
            new string('a', 64),
            [
                new("privacy.notice", 2),
                new("terms", 1)
            ]);

        projection.ApplyPolicy(PropertyProcessingStatus.Enabled, policy, 5);
        projection.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            CreatePolicy(
                new string('a', 64),
                [
                    new("terms", 1),
                    new("privacy.notice", 2)
                ]),
            5);
        projection.ApplyPolicy(
            PropertyProcessingStatus.Suspended,
            CreatePolicy(new string('b', 64), []),
            4);

        Assert.True(projection.IsKnown);
        Assert.Equal(PropertyProcessingStatus.Enabled, projection.ProcessingStatus);
        Assert.Equal(5, projection.PolicySourceVersion);
        Assert.Equal(new string('a', 64), projection.GovernancePolicy!.ContentSha256);
        Assert.Throws<InvalidOperationException>(() => projection.ApplyPolicy(
            PropertyProcessingStatus.Suspended,
            policy,
            5));
        Assert.Throws<InvalidOperationException>(() => projection.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            CreatePolicy(new string('b', 64), policy.Acknowledgements),
            5));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            policy,
            0));
    }

    private static DataRightsPropertyProjection CreatePlaceholder() => new(
        TenantId,
        Guid.NewGuid(),
        null,
        null,
        PropertyStatus.Unknown,
        0);

    private static PropertyGovernancePolicyBinding CreatePolicy(
        string contentSha256,
        IReadOnlyCollection<PropertyGovernanceAcknowledgement> acknowledgements)
    {
        DateTimeOffset effective = new(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        return new(
            "GB",
            "policy.gb",
            2,
            "eu-west",
            "standard",
            "guest-retention",
            3,
            contentSha256,
            effective,
            effective.AddYears(1),
            effective.AddDays(1),
            acknowledgements);
    }
}
