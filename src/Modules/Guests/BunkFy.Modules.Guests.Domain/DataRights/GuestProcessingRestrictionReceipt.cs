namespace BunkFy.Modules.Guests.Domain.DataRights;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestProcessingRestrictionReceipt : ScopedAggregateRoot<Guid>
{
    private GuestProcessingRestrictionReceipt() { }

    private GuestProcessingRestrictionReceipt(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid IdempotencyKey { get; private set; }
    public Guid RestrictionId { get; private set; }
    public GuestProcessingRestrictionAction Action { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid GuestId { get; private set; }
    public Guid CaseId { get; private set; }
    public long ApprovalRevision { get; private set; }
    public long SelectedGuestVersion { get; private set; }
    public long ResultingRestrictionVersion { get; private set; }
    public long ResultingProjectionRevision { get; private set; }
    public bool EffectiveRestricted { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public Guid EventId { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }

    public static Result<GuestProcessingRestrictionReceipt> Create(
        Guid receiptId,
        string tenantId,
        Guid idempotencyKey,
        Guid restrictionId,
        GuestProcessingRestrictionAction action,
        Guid propertyId,
        Guid guestId,
        Guid caseId,
        long approvalRevision,
        long selectedGuestVersion,
        int restrictionContractVersion,
        long resultingRestrictionVersion,
        long resultingProjectionRevision,
        bool effectiveRestricted,
        string actorId,
        Guid eventId,
        DateTimeOffset completedAtUtc)
    {
        if (receiptId == Guid.Empty ||
            idempotencyKey == Guid.Empty ||
            restrictionId == Guid.Empty ||
            propertyId == Guid.Empty ||
            guestId == Guid.Empty ||
            caseId == Guid.Empty ||
            eventId == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<GuestProcessingRestrictionReceipt>(
                GuestsDomainErrors.RestrictionReceiptIdentityInvalid);
        }

        bool restrictionVersionValid = action switch
        {
            GuestProcessingRestrictionAction.Apply => resultingRestrictionVersion == 1,
            GuestProcessingRestrictionAction.Release => resultingRestrictionVersion == 2,
            _ => false
        };
        if (approvalRevision < 1 ||
            selectedGuestVersion < 1 ||
            restrictionContractVersion < 1 ||
            resultingProjectionRevision < 1 ||
            !restrictionVersionValid)
        {
            return Result.Failure<GuestProcessingRestrictionReceipt>(
                GuestsDomainErrors.RestrictionReceiptVersionInvalid);
        }

        string? normalizedActorId = actorId?.Trim();
        if (normalizedActorId is not { Length: > 0 } ||
            normalizedActorId.Length > GuestProfile.ActorIdMaxLength ||
            completedAtUtc == default ||
            (action == GuestProcessingRestrictionAction.Apply && !effectiveRestricted))
        {
            return Result.Failure<GuestProcessingRestrictionReceipt>(
                GuestsDomainErrors.RestrictionReceiptTransitionInvalid);
        }

        GuestProcessingRestrictionReceipt receipt = new(receiptId, scopeId)
        {
            IdempotencyKey = idempotencyKey,
            RestrictionId = restrictionId,
            Action = action,
            PropertyId = propertyId,
            GuestId = guestId,
            CaseId = caseId,
            ApprovalRevision = approvalRevision,
            SelectedGuestVersion = selectedGuestVersion,
            ResultingRestrictionVersion = resultingRestrictionVersion,
            ResultingProjectionRevision = resultingProjectionRevision,
            EffectiveRestricted = effectiveRestricted,
            ActorId = normalizedActorId,
            EventId = eventId,
            CompletedAtUtc = completedAtUtc
        };
        receipt.RaiseDomainEvent(new GuestProcessingRestrictionChangedDomainEvent(
            eventId,
            completedAtUtc,
            scopeId,
            propertyId,
            guestId,
            restrictionContractVersion,
            resultingProjectionRevision,
            effectiveRestricted));
        return Result.Success(receipt);
    }
}
