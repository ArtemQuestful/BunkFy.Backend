namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.TimeZones;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneOutboxProjectorTests
{
    private static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dedicated_transition_and_generic_update_share_one_outbox_handler()
    {
        RecordingOutbox outbox = new();
        var projector = new PropertyUpdatedOutboxProjector(
            new RecordingOutboxRegistry(outbox),
            new FixedIdGenerator(DedicatedEventId));
        PropertyUpdatedDomainEvent domainEvent = new(
            GenericEventId,
            OccurredAtUtc,
            PropertyId,
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "Etc/UTC",
            PropertyState.Active,
            2,
            "UTC");

        await projector.HandleAsync(domainEvent, CancellationToken.None);

        Assert.Collection(
            outbox.Events,
            item =>
            {
                PropertyUpdatedIntegrationEvent generic =
                    Assert.IsType<PropertyUpdatedIntegrationEvent>(item);
                Assert.Equal(GenericEventId, generic.EventId);
                Assert.Equal("Etc/UTC", generic.TimeZoneId);
            },
            item =>
            {
                PropertyTimeZoneChangedIntegrationEvent dedicated =
                    Assert.IsType<
                        PropertyTimeZoneChangedIntegrationEvent>(item);
                Assert.Equal(DedicatedEventId, dedicated.EventId);
                Assert.Equal("UTC", dedicated.PreviousTimeZoneId);
                Assert.Equal(
                    "Etc/UTC",
                    dedicated.PreviousCanonicalTimeZoneId);
                Assert.Equal(
                    PropertyTimeZoneChangeKind.Canonicalized,
                    dedicated.ChangeKind);
                Assert.Equal(
                    TimeZoneCatalog.Default.CatalogVersion,
                    dedicated.CatalogVersion);
                Assert.Equal(2, dedicated.PropertyVersion);
            });
    }

    [Fact]
    public async Task Generic_details_update_does_not_fan_out_a_time_zone_event()
    {
        RecordingOutbox outbox = new();
        var projector = new PropertyUpdatedOutboxProjector(
            new RecordingOutboxRegistry(outbox),
            new ThrowingIdGenerator());
        PropertyUpdatedDomainEvent domainEvent = new(
            GenericEventId,
            OccurredAtUtc,
            PropertyId,
            "tenant-a",
            "Renamed Hostel",
            "hostel-one",
            "Etc/UTC",
            PropertyState.Active,
            2);

        await projector.HandleAsync(domainEvent, CancellationToken.None);

        Assert.IsType<PropertyUpdatedIntegrationEvent>(
            Assert.Single(outbox.Events));
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => PropertiesModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
    }

    private sealed class FixedIdGenerator(Guid value) : IIdGenerator
    {
        public Guid NewId() => value;
    }

    private sealed class ThrowingIdGenerator : IIdGenerator
    {
        public Guid NewId() => throw new InvalidOperationException(
            "A generic details update must not allocate a dedicated id.");
    }

    private static readonly Guid PropertyId = Guid.Parse(
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GenericEventId = Guid.Parse(
        "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid DedicatedEventId = Guid.Parse(
        "cccccccc-cccc-cccc-cccc-cccccccccccc");
}
