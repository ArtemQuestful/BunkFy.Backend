namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Events;
using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using BunkFy.TimeZones;

internal sealed class PropertyUpdatedOutboxProjector(
    IOutboxWriterRegistry outboxWriters,
    IIdGenerator ids)
    : IDomainEventHandler<PropertyUpdatedDomainEvent>
{
    public async Task HandleAsync(
        PropertyUpdatedDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        IOutboxWriter outbox = outboxWriters.GetRequired(
            PropertiesModuleMetadata.Name);
        await outbox.EnqueueAsync(
            new PropertyUpdatedIntegrationEvent(
                domainEvent.EventId,
                domainEvent.ScopeId,
                domainEvent.OccurredAtUtc,
                domainEvent.PropertyId,
                domainEvent.Name,
                domainEvent.Code,
                domainEvent.TimeZoneId,
                PropertiesMapper.MapStatus(domainEvent.Status),
                domainEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);

        if (domainEvent.PreviousTimeZoneId is null)
        {
            return;
        }

        TimeZoneCatalog catalog = TimeZoneCatalog.Default;
        string? previousCanonicalTimeZoneId = catalog.TryResolve(
                domainEvent.PreviousTimeZoneId,
                out TimeZoneCatalogResolution? previous)
            ? previous.CanonicalTimeZoneId
            : null;
        PropertyTimeZoneChangeKind changeKind = string.Equals(
                previousCanonicalTimeZoneId,
                domainEvent.TimeZoneId,
                StringComparison.Ordinal)
            ? PropertyTimeZoneChangeKind.Canonicalized
            : PropertyTimeZoneChangeKind.Changed;
        await outbox.EnqueueAsync(
            new PropertyTimeZoneChangedIntegrationEvent(
                ids.NewId(),
                domainEvent.ScopeId,
                domainEvent.OccurredAtUtc,
                domainEvent.PropertyId,
                domainEvent.PreviousTimeZoneId,
                previousCanonicalTimeZoneId,
                domainEvent.TimeZoneId,
                changeKind,
                catalog.CatalogVersion,
                domainEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);
    }
}
