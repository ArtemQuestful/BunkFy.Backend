namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Mapping;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Events;
using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;

internal sealed class GuestDataRightsCorrectionAppliedOutboxProjector(
    IOutboxWriterRegistry outboxWriters)
    : IDomainEventHandler<GuestDataRightsCorrectionAppliedDomainEvent>
{
    public Task HandleAsync(
        GuestDataRightsCorrectionAppliedDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        IOutboxWriter outbox = outboxWriters.GetRequired(GuestsModuleMetadata.Name);
        return outbox.EnqueueAsync(
            new DataRightsCorrectionAppliedIntegrationEvent(
                domainEvent.EventId,
                domainEvent.ScopeId,
                domainEvent.OccurredAtUtc,
                domainEvent.ExecutionId,
                domainEvent.PropertyId,
                domainEvent.CaseId,
                domainEvent.ApprovalRevision,
                GuestsDataRightsCoordinates.Owner,
                GuestsDataRightsCoordinates.GuestProfileRecordType,
                domainEvent.GuestId,
                domainEvent.SelectedRecordVersion,
                domainEvent.CurrentRecordVersion,
                GuestsDataRightsCoordinates.CorrectionFieldPolicyKey,
                domainEvent.ReceiptContractVersion,
                domainEvent.ReceiptId,
                domainEvent.ChangedFields.Select(field => field.ToFieldKey()).ToArray()),
            cancellationToken);
    }
}
