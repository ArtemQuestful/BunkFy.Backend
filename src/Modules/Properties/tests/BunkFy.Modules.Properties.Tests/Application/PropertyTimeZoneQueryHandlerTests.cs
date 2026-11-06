namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.TimeZones;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneQueryHandlerTests
{
    private static readonly DateTimeOffset CompletedAtUtc =
        new(2026, 8, 13, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Compliance_derives_versioned_health_and_correction_capability()
    {
        Guid activeId = Guid.NewGuid();
        Guid retiredId = Guid.NewGuid();
        StubComplianceReader reader = new(new(
            [
                new(
                    activeId,
                    "Active",
                    "active",
                    "UTC",
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    null,
                    4),
                new(
                    retiredId,
                    "Retired",
                    "retired",
                    "Pacific Standard Time",
                    PropertyStatus.Retired,
                    PropertyProcessingStatus.Suspended,
                    "US",
                    9)
            ],
            "next",
            HasMore: true));
        var handler = new ListPropertyTimeZoneComplianceQueryHandler(
            reader,
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCompliancePageDto> result =
            await handler.HandleAsync(
                new(null, 20),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            result.Value.CatalogVersion);
        Assert.Equal(CompletedAtUtc, result.Value.ObservedAtUtc);
        Assert.Equal("next", result.Value.NextCursor);
        Assert.True(result.Value.HasMore);
        PropertyTimeZoneComplianceItemDto active = Assert.Single(
            result.Value.Properties,
            item => item.PropertyId == activeId);
        Assert.Equal(PropertyTimeZoneStatus.Alias, active.TimeZoneStatus);
        Assert.Equal("Etc/UTC", active.CanonicalTimeZoneId);
        Assert.True(active.CorrectionAllowed);
        PropertyTimeZoneComplianceItemDto retired = Assert.Single(
            result.Value.Properties,
            item => item.PropertyId == retiredId);
        Assert.Equal(PropertyTimeZoneStatus.Legacy, retired.TimeZoneStatus);
        Assert.Null(retired.CanonicalTimeZoneId);
        Assert.False(retired.CorrectionAllowed);
        Assert.Equal("US", retired.OperatingCountryCode);
    }

    [Fact]
    public async Task Compliance_exposes_an_active_runtime_mismatch_as_correctable()
    {
        Guid propertyId = Guid.NewGuid();
        var handler = new ListPropertyTimeZoneComplianceQueryHandler(
            new StubComplianceReader(new(
                [new(
                    propertyId,
                    "Active",
                    "active",
                    "Europe/London",
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    null,
                    4)],
                null,
                HasMore: false)),
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.IncompatibleProbe());

        Result<PropertyTimeZoneCompliancePageDto> result =
            await handler.HandleAsync(new(null, 20), CancellationToken.None);

        PropertyTimeZoneComplianceItemDto item = Assert.Single(
            result.Value.Properties);
        Assert.Equal(
            PropertyTimeZoneStatus.RuntimeUnavailable,
            item.TimeZoneStatus);
        Assert.True(item.CorrectionAllowed);
        Assert.Equal(CompletedAtUtc, result.Value.ObservedAtUtc);
    }

    [Fact]
    public async Task Compliance_maps_strict_cursor_rejection_to_query_invalid()
    {
        var handler = new ListPropertyTimeZoneComplianceQueryHandler(
            new StubComplianceReader(
                new([], null, HasMore: false),
                new ArgumentException("tenant-bound cursor mismatch")),
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCompliancePageDto> result =
            await handler.HandleAsync(
                new("opaque-cross-tenant-cursor", 20),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeZoneQueryInvalid,
            result.Error);
    }

    [Fact]
    public async Task Compliance_never_publishes_an_invalid_observation_time()
    {
        var handler = new ListPropertyTimeZoneComplianceQueryHandler(
            new StubComplianceReader(new([], null, HasMore: false)),
            new FixedClock(default),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneCompliancePageDto> result =
            await handler.HandleAsync(new(null, 20), CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
    }

    [Fact]
    public async Task Recovery_returns_immutable_receipt_and_current_health()
    {
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        PropertyTimeZoneRevisionReadModel revision = Revision(
            propertyId,
            operationId);
        Property current = CreateProperty(
            propertyId,
            "Europe/Paris",
            version: 7);
        StubPropertiesReadRepository properties = new(current);
        var handler = new GetPropertyTimeZoneRecoveryQueryHandler(
            new StubRevisionReader(revision),
            properties,
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneRecoveryDto> result = await handler.HandleAsync(
            new(propertyId, operationId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(operationId, result.Value.Receipt.OperationId);
        Assert.Equal("Europe/London", result.Value.Receipt.TimeZoneId);
        Assert.Equal("operator:one", result.Value.Receipt.ActorId);
        Assert.Equal(2, result.Value.Receipt.Version);
        Assert.Equal("Europe/Paris", result.Value.CurrentTimeZoneId);
        Assert.Equal(7, result.Value.CurrentVersion);
        Assert.Equal(
            PropertyTimeZoneStatus.Canonical,
            result.Value.CurrentTimeZoneStatus);
        Assert.Equal(
            CompletedAtUtc,
            result.Value.CurrentTimeZoneObservedAtUtc);
        Assert.Equal(1, properties.Reads);
    }

    [Fact]
    public async Task Missing_operation_does_not_disclose_or_read_property_state()
    {
        Guid propertyId = Guid.NewGuid();
        StubPropertiesReadRepository properties = new(CreateProperty(
            propertyId,
            "Etc/UTC",
            version: 1));
        var handler = new GetPropertyTimeZoneRecoveryQueryHandler(
            new StubRevisionReader(null),
            properties,
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneRecoveryDto> result = await handler.HandleAsync(
            new(propertyId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeZoneOperationNotFound,
            result.Error);
        Assert.Equal(0, properties.Reads);
    }

    [Fact]
    public async Task Recovery_reports_missing_current_property_after_operation_lookup()
    {
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        var handler = new GetPropertyTimeZoneRecoveryQueryHandler(
            new StubRevisionReader(Revision(propertyId, operationId)),
            new StubPropertiesReadRepository(null),
            new TestClock(),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneRecoveryDto> result = await handler.HandleAsync(
            new(propertyId, operationId),
            CancellationToken.None);

        Assert.Equal(PropertiesDomainErrors.PropertyNotFound, result.Error);
    }

    [Fact]
    public async Task Recovery_never_publishes_an_invalid_observation_time()
    {
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        var handler = new GetPropertyTimeZoneRecoveryQueryHandler(
            new StubRevisionReader(Revision(propertyId, operationId)),
            new StubPropertiesReadRepository(CreateProperty(
                propertyId,
                "Europe/Paris",
                version: 1)),
            new FixedClock(default),
            PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyTimeZoneRecoveryDto> result = await handler.HandleAsync(
            new(propertyId, operationId),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
    }

    private static PropertyTimeZoneRevisionReadModel Revision(
        Guid propertyId,
        Guid operationId) => new(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            operationId,
            PropertyTimeZoneChangeKind.Changed,
            "Europe/London",
            "Etc/UTC",
            "Europe/London",
            TimeZoneCatalog.Default.CatalogVersion,
            1,
            2,
            "operator:one",
            CompletedAtUtc);

    private static Property CreateProperty(
        Guid propertyId,
        string timeZoneId,
        long version)
    {
        Property property = Property.Create(
            propertyId,
            "tenant-a",
            "Hostel One",
            "hostel-one",
            timeZoneId,
            Guid.NewGuid(),
            CompletedAtUtc.AddDays(-30)).Value;
        while (property.Version < version)
        {
            Assert.True(property.RegisterRoom(property.Version).IsSuccess);
        }

        property.ClearDomainEvents();
        return property;
    }

    private sealed class StubComplianceReader(
        PropertyTimeZoneComplianceReadPage page,
        ArgumentException? exception = null)
        : IPropertyTimeZoneComplianceReader
    {
        public Task<PropertyTimeZoneComplianceReadPage> ReadPageAsync(
            string? cursor,
            int pageSize,
            string catalogVersion,
            CancellationToken cancellationToken) =>
            exception is null
                ? Task.FromResult(page)
                : Task.FromException<PropertyTimeZoneComplianceReadPage>(
                    exception);
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => CompletedAtUtc;
    }

    private sealed class FixedClock(DateTimeOffset value) : ISystemClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class StubRevisionReader(
        PropertyTimeZoneRevisionReadModel? revision)
        : IPropertyTimeZoneRevisionReader
    {
        public Task<PropertyTimeZoneRevisionReadModel?> GetAsync(
            Guid propertyId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
                revision is not null &&
                revision.PropertyId == propertyId &&
                revision.OperationId == operationId
                    ? revision
                    : null);
    }

    private sealed class StubPropertiesReadRepository(Property? property)
        : IPropertiesReadRepository
    {
        public int Reads { get; private set; }

        public Task<Property?> GetPropertyAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(
                property?.Id == propertyId ? property : null);
        }

        public Task<PropertyReadPage> ListPropertiesAsync(
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
}
