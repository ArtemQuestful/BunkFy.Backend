namespace BunkFy.Modules.Staff.Tests.Persistence;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Persistence;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffPropertyProjectionIntegrityTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public void Projection_requires_valid_coordinates_lifecycle_and_payload()
    {
        Assert.Throws<ArgumentException>(() => new StaffPropertyProjection(
            " ",
            Guid.NewGuid(),
            "Hostel",
            PropertyStatus.Active,
            1));
        Assert.Throws<ArgumentException>(() => new StaffPropertyProjection(
            TenantId,
            Guid.Empty,
            "Hostel",
            PropertyStatus.Active,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaffPropertyProjection(
                TenantId,
                Guid.NewGuid(),
                "Hostel",
                PropertyStatus.Active,
                0));
        Assert.Throws<ArgumentException>(() => new StaffPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            null,
            PropertyStatus.Active,
            1));
        Assert.Throws<ArgumentException>(() => new StaffPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            "Hostel",
            PropertyStatus.Unknown,
            1));
        Assert.Throws<ArgumentException>(() => new StaffPropertyProjection(
            TenantId,
            Guid.NewGuid(),
            "Hostel\nOther",
            PropertyStatus.Active,
            1));

        StaffPropertyProjection retired = new(
            " tenant-a ",
            Guid.NewGuid(),
            null,
            PropertyStatus.Retired,
            1);
        Assert.Equal(TenantId, retired.ScopeId);
        Assert.Null(retired.Name);
    }

    [Fact]
    public void Projection_distinguishes_stale_replay_and_equal_version_conflict()
    {
        StaffPropertyProjection projection = new(
            TenantId,
            Guid.NewGuid(),
            "Hostel",
            PropertyStatus.Active,
            3);

        projection.Apply(" Hostel ", PropertyStatus.Active, 3);
        projection.Apply("Stale", PropertyStatus.Retired, 2);

        Assert.Equal("Hostel", projection.Name);
        Assert.Equal(PropertyStatus.Active, projection.Status);
        Assert.Equal(3, projection.Version);
        Assert.Throws<InvalidOperationException>(() => projection.Apply(
            "Another hostel",
            PropertyStatus.Active,
            3));
        Assert.Throws<InvalidOperationException>(() => projection.Apply(
            null,
            PropertyStatus.Retired,
            3));

        projection.Apply(null, PropertyStatus.Retired, 4);
        projection.Apply(string.Empty, PropertyStatus.Retired, 4);

        Assert.Equal("Hostel", projection.Name);
        Assert.Equal(PropertyStatus.Retired, projection.Status);
        Assert.Equal(4, projection.Version);
        Assert.Throws<ArgumentException>(() => projection.Apply(
            null,
            PropertyStatus.Active,
            5));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.Apply(
            "Hostel",
            PropertyStatus.Active,
            0));
    }
}
