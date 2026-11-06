namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesObservationTimeTests
{
    [Fact]
    public void Invalid_clock_values_are_rejected()
    {
        foreach (DateTimeOffset value in InvalidValues())
        {
            Assert.False(PropertiesObservationTime.IsValid(value));
        }
    }

    [Fact]
    public void Property_mapping_never_publishes_an_invalid_observation()
    {
        Property property = Property.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "Etc/UTC",
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)).Value;

        foreach (DateTimeOffset value in InvalidValues())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PropertiesMapper.ToDto(
                    property,
                    value,
                    PropertyTimeZoneHealthClassifierTests.CompatibleProbe()));
        }
    }

    private static IEnumerable<DateTimeOffset> InvalidValues()
    {
        yield return default;
        yield return new DateTimeOffset(
            2026,
            8,
            14,
            12,
            0,
            0,
            TimeSpan.FromHours(2));
        yield return new DateTimeOffset(
            1899,
            12,
            31,
            23,
            59,
            59,
            TimeSpan.Zero);
        yield return new DateTimeOffset(
            9994,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
    }
}
