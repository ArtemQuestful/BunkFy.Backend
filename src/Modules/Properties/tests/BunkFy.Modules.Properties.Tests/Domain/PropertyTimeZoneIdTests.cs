namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneIdTests
{
    [Theory]
    [InlineData("Etc/UTC", "Etc/UTC")]
    [InlineData(" UTC ", "Etc/UTC")]
    [InlineData("Asia/Calcutta", "Asia/Kolkata")]
    public void Strict_creation_stores_the_primary_tzdb_identifier(
        string input,
        string expected)
    {
        var result = PropertyTimeZoneId.Create(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Value);
    }

    [Theory]
    [InlineData("Pacific Standard Time")]
    [InlineData("Custom/OperatorZone")]
    [InlineData("europe/london")]
    public void Strict_creation_rejects_non_tzdb_or_case_mismatched_ids(
        string input)
    {
        var result = PropertyTimeZoneId.Create(input);

        Assert.Equal(PropertiesDomainErrors.TimeZoneInvalid, result.Error);
    }

    [Theory]
    [InlineData(" UTC ", "UTC")]
    [InlineData(" Pacific Standard Time ", "Pacific Standard Time")]
    [InlineData(" Custom/OperatorZone ", "Custom/OperatorZone")]
    public void Persisted_restore_preserves_the_exact_trimmed_identifier(
        string input,
        string expected) =>
        Assert.Equal(
            expected,
            PropertyTimeZoneId.RestorePersisted(input).Value);

    [Fact]
    public void Persisted_restore_fails_loudly_only_for_structural_corruption()
    {
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneId.RestorePersisted(null!));
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneId.RestorePersisted("   "));
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneId.RestorePersisted("Europe/\u0000London"));
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneId.RestorePersisted(
                new string('x', Property.TimeZoneIdMaxLength + 1)));
    }
}
