namespace BunkFy.Modules.Guests.Application.Handlers;

using Gma.Framework.Messaging;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Properties.Contracts;

[IntegrationEventHandler(GuestsModuleMetadata.PropertyCreatedHandlerName)]
internal sealed class GuestPropertyCreatedHandler(IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyCreatedIntegrationEvent>
{
    public async Task HandleAsync(
        PropertyCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        await properties.ApplyTopologyAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                integrationEvent.Name,
                integrationEvent.TimeZoneId,
                integrationEvent.Status,
                integrationEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);
        await properties.ApplyTimeZoneAsync(
            GuestPropertyTimeZoneEvidenceClassifier.Classify(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                integrationEvent.TimeZoneId,
                GuestPropertyTimeZoneEvidenceSource.Generic,
                integrationEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);
    }
}

[IntegrationEventHandler(GuestsModuleMetadata.PropertyUpdatedHandlerName)]
internal sealed class GuestPropertyUpdatedHandler(IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyUpdatedIntegrationEvent>
{
    public async Task HandleAsync(
        PropertyUpdatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        await properties.ApplyTopologyAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                integrationEvent.Name,
                integrationEvent.TimeZoneId,
                integrationEvent.Status,
                integrationEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);
        await properties.ApplyTimeZoneAsync(
            GuestPropertyTimeZoneEvidenceClassifier.Classify(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                integrationEvent.TimeZoneId,
                GuestPropertyTimeZoneEvidenceSource.Generic,
                integrationEvent.PropertyVersion),
            cancellationToken).ConfigureAwait(false);
    }
}

[IntegrationEventHandler(
    GuestsModuleMetadata.PropertyTimeZoneChangedHandlerName)]
internal sealed class GuestPropertyTimeZoneChangedHandler(
    IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyTimeZoneChangedIntegrationEvent>
{
    public Task HandleAsync(
        PropertyTimeZoneChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken) =>
        properties.ApplyTimeZoneAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                integrationEvent.TimeZoneId,
                integrationEvent.TimeZoneId,
                PropertyTimeZoneStatus.Canonical,
                integrationEvent.CatalogVersion,
                GuestPropertyTimeZoneEvidenceSource.Dedicated,
                integrationEvent.PropertyVersion),
            cancellationToken);
}

[IntegrationEventHandler(GuestsModuleMetadata.PropertyRetiredHandlerName)]
internal sealed class GuestPropertyRetiredHandler(IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyRetiredIntegrationEvent>
{
    public Task HandleAsync(PropertyRetiredIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        properties.ApplyTopologyAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                string.Empty,
                TimeZoneId: null,
                PropertyStatus.Retired,
                integrationEvent.PropertyVersion),
            cancellationToken);
}

[IntegrationEventHandler(GuestsModuleMetadata.PropertyProcessingPolicyActivatedHandlerName)]
internal sealed class GuestPropertyProcessingPolicyActivatedHandler(IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyProcessingPolicyActivatedIntegrationEvent>
{
    public Task HandleAsync(
        PropertyProcessingPolicyActivatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken) =>
        properties.ApplyPolicyAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                PropertyProcessingStatus.Enabled,
                integrationEvent.Binding,
                integrationEvent.PropertyVersion),
            cancellationToken);
}

[IntegrationEventHandler(GuestsModuleMetadata.PropertyProcessingSuspendedHandlerName)]
internal sealed class GuestPropertyProcessingSuspendedHandler(IGuestPropertyProjectionRepository properties)
    : IIntegrationEventHandler<PropertyProcessingSuspendedIntegrationEvent>
{
    public Task HandleAsync(
        PropertyProcessingSuspendedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken) =>
        properties.ApplyPolicyAsync(
            new(
                integrationEvent.ScopeId,
                integrationEvent.PropertyId,
                PropertyProcessingStatus.Suspended,
                integrationEvent.Binding,
                integrationEvent.PropertyVersion),
            cancellationToken);
}
