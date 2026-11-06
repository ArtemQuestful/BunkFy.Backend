namespace BunkFy.Modules.Guests.Domain.DataRights;

using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestDataRightsCorrectionReceipt : ScopedAggregateRoot<Guid>
{
    public const int CurrentContractVersion = 2;
    public const int LegacyChangedFieldsMask = (1 << 8) - 1;
    public const int AllChangedFieldsMask = (1 << 9) - 1;

    private GuestDataRightsCorrectionReceipt() { }

    private GuestDataRightsCorrectionReceipt(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid CaseId { get; private set; }
    public long ApprovalRevision { get; private set; }
    public Guid GuestId { get; private set; }
    public long SelectedRecordVersion { get; private set; }
    public long CurrentRecordVersion { get; private set; }
    public int ChangedFieldsMask { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CompletionEventId { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }

    public IReadOnlyCollection<GuestProfileField> ChangedFields =>
        Enum.GetValues<GuestProfileField>()
            .Where(candidate => candidate is not GuestProfileField.Unknown &&
                (this.ChangedFieldsMask & ToMask(candidate)) != 0)
            .ToArray();

    public static Result<GuestDataRightsCorrectionReceipt> Create(
        Guid receiptId,
        string tenantId,
        Guid idempotencyKey,
        Guid propertyId,
        Guid caseId,
        long approvalRevision,
        Guid guestId,
        long selectedRecordVersion,
        long currentRecordVersion,
        IReadOnlyCollection<GuestProfileField> changedFields,
        Guid eventId,
        Guid completionEventId,
        DateTimeOffset completedAtUtc)
    {
        if (receiptId == Guid.Empty ||
            idempotencyKey == Guid.Empty ||
            propertyId == Guid.Empty ||
            caseId == Guid.Empty ||
            guestId == Guid.Empty ||
            eventId == Guid.Empty ||
            completionEventId == Guid.Empty ||
            completionEventId == eventId ||
            completedAtUtc == default ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<GuestDataRightsCorrectionReceipt>(
                GuestsDomainErrors.CorrectionReceiptIdentityInvalid);
        }

        if (approvalRevision < 1 ||
            selectedRecordVersion < 1 ||
            currentRecordVersion != selectedRecordVersion + 1)
        {
            return Result.Failure<GuestDataRightsCorrectionReceipt>(
                GuestsDomainErrors.CorrectionReceiptVersionInvalid);
        }

        int changedFieldsMask = ToMask(changedFields);
        if (changedFieldsMask is <= 0 or > AllChangedFieldsMask)
        {
            return Result.Failure<GuestDataRightsCorrectionReceipt>(
                GuestsDomainErrors.CorrectionReceiptFieldsInvalid);
        }

        GuestDataRightsCorrectionReceipt receipt = new(receiptId, scopeId)
        {
            ContractVersion = CurrentContractVersion,
            IdempotencyKey = idempotencyKey,
            PropertyId = propertyId,
            CaseId = caseId,
            ApprovalRevision = approvalRevision,
            GuestId = guestId,
            SelectedRecordVersion = selectedRecordVersion,
            CurrentRecordVersion = currentRecordVersion,
            ChangedFieldsMask = changedFieldsMask,
            EventId = eventId,
            CompletionEventId = completionEventId,
            CompletedAtUtc = completedAtUtc
        };
        receipt.RaiseDomainEvent(new GuestDataRightsCorrectionAppliedDomainEvent(
            completionEventId,
            completedAtUtc,
            scopeId,
            idempotencyKey,
            receiptId,
            propertyId,
            caseId,
            approvalRevision,
            guestId,
            selectedRecordVersion,
            currentRecordVersion,
            changedFields,
            receipt.ContractVersion));
        return Result.Success(receipt);
    }

    private static int ToMask(IReadOnlyCollection<GuestProfileField> fields)
    {
        if (fields is null || fields.Count == 0 || fields.Any(candidate =>
                candidate is GuestProfileField.Unknown || !Enum.IsDefined(candidate)))
        {
            return 0;
        }

        return fields.Aggregate(0, (mask, candidate) => mask | ToMask(candidate));
    }

    private static int ToMask(GuestProfileField candidate) => 1 << ((int)candidate - 1);
}
