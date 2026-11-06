namespace BunkFy.Modules.Guests.Domain.Events;

using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Domain;

public sealed record GuestDataRightsCorrectionAppliedDomainEvent : ScopedDomainEvent
{
    public GuestDataRightsCorrectionAppliedDomainEvent(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        string scopeId,
        Guid executionId,
        Guid receiptId,
        Guid propertyId,
        Guid caseId,
        long approvalRevision,
        Guid guestId,
        long selectedRecordVersion,
        long currentRecordVersion,
        IReadOnlyCollection<GuestProfileField> changedFields,
        int receiptContractVersion = 1)
        : base(eventId, occurredAtUtc, scopeId)
    {
        if (executionId == Guid.Empty ||
            receiptId == Guid.Empty ||
            propertyId == Guid.Empty ||
            caseId == Guid.Empty ||
            guestId == Guid.Empty ||
            approvalRevision < 1 ||
            selectedRecordVersion < 1 ||
            currentRecordVersion != selectedRecordVersion + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRecordVersion));
        }

        ArgumentNullException.ThrowIfNull(changedFields);
        GuestProfileField[] fields = changedFields
            .Distinct()
            .Order()
            .ToArray();
        if (receiptContractVersion is not (1 or 2) || fields.Length == 0 || fields.Any(field =>
                field is GuestProfileField.Unknown || !Enum.IsDefined(field) ||
                (receiptContractVersion == 1 && field == GuestProfileField.LanguageTags)))
        {
            throw new ArgumentException(
                "At least one correctable Guest field is required.",
                nameof(changedFields));
        }

        this.ExecutionId = executionId;
        this.ReceiptId = receiptId;
        this.PropertyId = propertyId;
        this.CaseId = caseId;
        this.ApprovalRevision = approvalRevision;
        this.GuestId = guestId;
        this.SelectedRecordVersion = selectedRecordVersion;
        this.CurrentRecordVersion = currentRecordVersion;
        this.ChangedFields = Array.AsReadOnly(fields);
        this.ReceiptContractVersion = receiptContractVersion;
    }

    public Guid ExecutionId { get; }
    public Guid ReceiptId { get; }
    public Guid PropertyId { get; }
    public Guid CaseId { get; }
    public long ApprovalRevision { get; }
    public Guid GuestId { get; }
    public long SelectedRecordVersion { get; }
    public long CurrentRecordVersion { get; }
    public IReadOnlyCollection<GuestProfileField> ChangedFields { get; }
    public int ReceiptContractVersion { get; }
}
