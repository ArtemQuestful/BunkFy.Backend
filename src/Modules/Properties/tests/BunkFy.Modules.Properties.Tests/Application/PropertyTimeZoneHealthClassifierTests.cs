namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneHealthClassifierTests
{
    [Fact]
    public void Active_runtime_unavailable_can_be_moved_to_a_compatible_zone()
    {
        PropertyTimeZoneHealth health =
            PropertyTimeZoneHealthClassifier.Classify(
                "Europe/London",
                correctionAllowed: true,
                ObservedAtUtc,
                IncompatibleProbe());

        Assert.Equal(
            PropertyTimeZoneStatus.RuntimeUnavailable,
            health.Status);
        Assert.Equal("Europe/London", health.CanonicalTimeZoneId);
        Assert.True(health.CorrectionAllowed);
    }

    [Fact]
    public void Retired_runtime_unavailable_is_not_presented_as_correctable()
    {
        PropertyTimeZoneHealth health =
            PropertyTimeZoneHealthClassifier.Classify(
                "Europe/London",
                correctionAllowed: false,
                ObservedAtUtc,
                IncompatibleProbe());

        Assert.Equal(
            PropertyTimeZoneStatus.RuntimeUnavailable,
            health.Status);
        Assert.False(health.CorrectionAllowed);
    }

    [Theory]
    [InlineData("Pacific Standard Time", PropertyTimeZoneStatus.Legacy)]
    [InlineData("Custom/OperatorZone", PropertyTimeZoneStatus.Unrecognized)]
    public void Unknown_catalog_classification_is_independent_of_host_runtime(
        string timeZoneId,
        PropertyTimeZoneStatus expected)
    {
        PropertyTimeZoneHealth available =
            PropertyTimeZoneHealthClassifier.Classify(
                timeZoneId,
                correctionAllowed: true,
                ObservedAtUtc,
                CompatibleProbe());
        PropertyTimeZoneHealth unavailable =
            PropertyTimeZoneHealthClassifier.Classify(
                timeZoneId,
                correctionAllowed: true,
                ObservedAtUtc,
                IncompatibleProbe());

        Assert.Equal(expected, available.Status);
        Assert.Equal(expected, unavailable.Status);
        Assert.True(available.CorrectionAllowed);
        Assert.True(unavailable.CorrectionAllowed);
    }

    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    internal static TimeZoneRuntimeCompatibilityProbe CompatibleProbe() =>
        TimeZoneRuntimeCompatibilityProbe.CreateForTesting(
            TimeZoneInfo.FindSystemTimeZoneById);

    internal static TimeZoneRuntimeCompatibilityProbe IncompatibleProbe() =>
        TimeZoneRuntimeCompatibilityProbe.CreateForTesting(_ =>
            TimeZoneInfo.CreateCustomTimeZone(
                "Deliberately mismatched test zone",
                TimeSpan.FromHours(9),
                "Deliberately mismatched test zone",
                "Deliberately mismatched test zone"));
}
