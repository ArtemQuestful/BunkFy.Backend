namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Events;
using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;

internal sealed class RetentionRunRetryRequestedOutboxProjector(
    IOutboxWriterRegistry outboxWriters)
    : IDomainEventHandler<RetentionRunRetryRequestedDomainEvent>
{
    public Task HandleAsync(
        RetentionRunRetryRequestedDomainEvent domainEvent,
        CancellationToken cancellationToken) =>
        outboxWriters.GetRequired(RetentionModuleMetadata.Name).EnqueueAsync(
            new RetentionRunRetryRequestedIntegrationEvent(
                domainEvent.EventId,
                domainEvent.ScopeId,
                domainEvent.OccurredAtUtc,
                domainEvent.RequestId,
                domainEvent.RunId,
                domainEvent.Attempt),
            cancellationToken);
}
