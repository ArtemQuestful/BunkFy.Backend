namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneCatalogQueryHandlerTests
{
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Catalog_cursor_binds_normalized_search_and_country_code()
    {
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(ObservedAtUtc),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCatalogPageDto> first = await handler.HandleAsync(
            new(" america ", " us ", null, 1),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(ObservedAtUtc, first.Value.ObservedAtUtc);
        Assert.True(first.Value.HasMore);
        Assert.NotNull(first.Value.NextCursor);
        Assert.Single(first.Value.TimeZones);

        Result<PropertyTimeZoneCatalogPageDto> second = await handler.HandleAsync(
            new("AMERICA", "US", first.Value.NextCursor, 1),
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.DoesNotContain(
            second.Value.TimeZones,
            item => item.TimeZoneId == first.Value.TimeZones.Single().TimeZoneId);

        Result<PropertyTimeZoneCatalogPageDto> mismatched = await handler.HandleAsync(
            new("EUROPE", "US", first.Value.NextCursor, 1),
            CancellationToken.None);

        Assert.True(mismatched.IsFailure);
    }

    [Theory]
    [InlineData("N1")]
    [InlineData("NLD")]
    [InlineData("N")]
    public async Task Catalog_rejects_malformed_country_codes(string countryCode)
    {
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(ObservedAtUtc),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCatalogPageDto> result = await handler.HandleAsync(
            new(null, countryCode, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Catalog_exposes_host_runtime_availability()
    {
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(ObservedAtUtc),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCatalogPageDto> result = await handler.HandleAsync(
            new("Etc/UTC", null, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PropertyTimeZoneCatalogItemDto utc = Assert.Single(
            result.Value.TimeZones,
            item => item.TimeZoneId == "Etc/UTC");
        Assert.True(utc.RuntimeAvailable);
        Assert.Equal(0, utc.UtcOffsetMinutes);
    }

    [Fact]
    public async Task Catalog_exposes_runtime_unavailability_without_changing_tzdb_data()
    {
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(ObservedAtUtc),
            (_, _) => false);

        Result<PropertyTimeZoneCatalogPageDto> result = await handler.HandleAsync(
            new("Etc/UTC", null, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PropertyTimeZoneCatalogItemDto utc = Assert.Single(
            result.Value.TimeZones,
            item => item.TimeZoneId == "Etc/UTC");
        Assert.False(utc.RuntimeAvailable);
        Assert.Equal(0, utc.UtcOffsetMinutes);
        Assert.Equal(ObservedAtUtc, result.Value.ObservedAtUtc);
    }

    [Fact]
    public async Task Catalog_observes_each_page_at_fresh_server_time()
    {
        DateTimeOffset nextClockValue =
            ObservedAtUtc.AddYears(1).AddHours(1);
        var clock = new AdvancingClock(ObservedAtUtc, nextClockValue);
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            clock,
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCatalogPageDto> first = await handler.HandleAsync(
            new(null, null, null, 1),
            CancellationToken.None);
        Result<PropertyTimeZoneCatalogPageDto> second = await handler.HandleAsync(
            new(null, null, first.Value.NextCursor, 1),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(ObservedAtUtc, first.Value.ObservedAtUtc);
        Assert.Equal(nextClockValue, second.Value.ObservedAtUtc);
        Assert.Equal(2, clock.Reads);
    }

    [Fact]
    public async Task Catalog_cursor_rejects_an_injected_observation_time()
    {
        var clock = new TestClock(ObservedAtUtc);
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            clock,
            (_, _) => true);
        Result<PropertyTimeZoneCatalogPageDto> first =
            await handler.HandleAsync(
                new(null, null, null, 1),
                CancellationToken.None);
        string cursor = Assert.IsType<string>(first.Value.NextCursor);
        string json = DecodeCursor(cursor);
        string forgedJson = json.Insert(
            json.Length - 1,
            ",\"observedAtUtc\":\"1900-01-01T00:00:00Z\"");
        string forged = EncodeCursor(forgedJson);

        Result<PropertyTimeZoneCatalogPageDto> result =
            await handler.HandleAsync(
                new(null, null, forged, 1),
                CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Catalog_probes_runtime_only_for_the_returned_page()
    {
        int probeCalls = 0;
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(ObservedAtUtc),
            (_, _) =>
            {
                probeCalls++;
                return true;
            });

        Result<PropertyTimeZoneCatalogPageDto> result =
            await handler.HandleAsync(
                new(null, null, null, 7),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.TimeZones.Count);
        Assert.Equal(result.Value.TimeZones.Count, probeCalls);
    }

    [Fact]
    public async Task Catalog_fails_closed_when_the_server_clock_is_invalid()
    {
        int probeCalls = 0;
        var handler = new ListPropertyTimeZoneCatalogQueryHandler(
            new TestClock(default),
            (_, _) =>
            {
                probeCalls++;
                return true;
            });

        Result<PropertyTimeZoneCatalogPageDto> result =
            await handler.HandleAsync(
                new(null, null, null, 7),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.Equal(0, probeCalls);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static string DecodeCursor(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(padded));
    }

    private static string EncodeCursor(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class AdvancingClock(
        DateTimeOffset first,
        DateTimeOffset later) : ISystemClock
    {
        public int Reads { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                this.Reads++;
                return this.Reads == 1 ? first : later;
            }
        }
    }
}
