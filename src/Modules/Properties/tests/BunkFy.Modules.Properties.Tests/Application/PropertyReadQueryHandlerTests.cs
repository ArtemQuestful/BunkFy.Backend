namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Tests;
using BunkFy.TimeZones;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyReadQueryHandlerTests
{
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Detail_maps_raw_legacy_state_with_application_observation()
    {
        Guid propertyId = Guid.NewGuid();
        Property property = LegacyPropertyTestFactory.Create(
            propertyId,
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "UTC",
            ObservedAtUtc.AddDays(-1));
        var handler = new GetPropertyQueryHandler(
            new StubReadRepository(property),
            new FixedClock(ObservedAtUtc),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyDto> result = await handler.HandleAsync(
            new(propertyId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("UTC", result.Value.TimeZoneId);
        Assert.Equal(PropertyTimeZoneStatus.Alias, result.Value.TimeZoneStatus);
        Assert.Equal("Etc/UTC", result.Value.CanonicalTimeZoneId);
        Assert.Equal(ObservedAtUtc, result.Value.TimeZoneObservedAtUtc);
        Assert.True(result.Value.TimeZoneCorrectionAllowed);
    }

    [Fact]
    public async Task Detail_returns_typed_time_source_failure()
    {
        Property property = LegacyPropertyTestFactory.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "UTC",
            ObservedAtUtc.AddDays(-1));
        var handler = new GetPropertyQueryHandler(
            new StubReadRepository(property),
            new FixedClock(default),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyDto> result = await handler.HandleAsync(
            new(property.Id),
            CancellationToken.None);

        Assert.Equal(PropertiesApplicationErrors.TimeSourceUnavailable, result.Error);
    }

    [Fact]
    public async Task List_maps_only_the_bounded_raw_page_with_one_observation()
    {
        Guid propertyId = Guid.NewGuid();
        var page = new PropertyReadPage(
            [new(
                propertyId,
                "Hostel One",
                "hostel-one",
                "UTC",
                PropertyState.Active,
                PropertyProcessingState.Unconfigured,
                Version: 4)],
            Page: 2,
            PageSize: 20,
            HasMore: true);
        var handler = new ListPropertiesQueryHandler(
            new StubReadRepository(page: page),
            new FixedClock(ObservedAtUtc),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new(Page: 2, PageSize: 20),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Page);
        Assert.Equal(20, result.Value.PageSize);
        Assert.True(result.Value.HasMore);
        PropertyListItemDto item = Assert.Single(result.Value.Properties);
        Assert.Equal(propertyId, item.PropertyId);
        Assert.Equal(PropertyTimeZoneStatus.Alias, item.TimeZoneStatus);
        Assert.Equal("Etc/UTC", item.CanonicalTimeZoneId);
        Assert.Equal(ObservedAtUtc, item.TimeZoneObservedAtUtc);
    }

    [Fact]
    public async Task List_returns_typed_time_source_failure()
    {
        var handler = new ListPropertiesQueryHandler(
            new StubReadRepository(page: new([], 1, 20, false)),
            new FixedClock(new DateTimeOffset(
                2026,
                8,
                14,
                12,
                0,
                0,
                TimeSpan.FromHours(1))),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new(Page: 1, PageSize: 20),
            CancellationToken.None);

        Assert.Equal(PropertiesApplicationErrors.TimeSourceUnavailable, result.Error);
    }

    private sealed class StubReadRepository(
        Property? property = null,
        PropertyReadPage? page = null) : IPropertiesReadRepository
    {
        public Task<Property?> GetPropertyAsync(
            Guid propertyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(property?.Id == propertyId ? property : null);

        public Task<PropertyReadPage> ListPropertiesAsync(
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            Task.FromResult(page ?? new([], pageRequest.Page, pageRequest.PageSize, false));

        public Task<PropertyReadPage> ListVisiblePropertiesAsync(
            PageRequest pageRequest,
            PropertiesVisibilityScope visibility,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RoomDto?> GetRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RoomListResponse> ListRoomsAsync(
            Guid propertyId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BedListResponse> ListBedsAsync(
            Guid propertyId,
            Guid roomId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
