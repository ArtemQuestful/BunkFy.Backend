namespace BunkFy.Modules.Inventory.Persistence;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Domain;
using Gma.Framework.Naming;

public sealed class InventoryAllocationAmendmentDecision : IScopedEntity
{
    private InventoryAllocationAmendmentDecision() { }

    internal InventoryAllocationAmendmentDecision(InventoryAllocationAmendmentDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        this.ScopeId = TenantIds.Normalize(record.ScopeId);
        RequireId(record.AmendmentRequestId, nameof(record.AmendmentRequestId));
        RequireId(record.AllocationId, nameof(record.AllocationId));
        RequireId(record.ReservationId, nameof(record.ReservationId));
        RequireId(record.PropertyId, nameof(record.PropertyId));
        if (!IsCanonicalFingerprint(record.RequestFingerprint))
        {
            throw new ArgumentException(
                "An allocation amendment decision requires a canonical request fingerprint.",
                nameof(record));
        }

        if (record.Confirmed
                ? record.RejectionReason is not null ||
                  record.AllocationVersion is null or <= 0
                : record.RejectionReason is null ||
                  record.RejectionReason <= InventoryAllocationRejectionReason.Unknown ||
                  !Enum.IsDefined(record.RejectionReason.Value) ||
                  record.AllocationVersion is not null)
        {
            throw new ArgumentException(
                "An allocation amendment decision has an inconsistent outcome.",
                nameof(record));
        }

        if (record.DecidedAtUtc == default)
        {
            throw new ArgumentException(
                "An allocation amendment decision requires a decision timestamp.",
                nameof(record));
        }

        this.Id = record.AmendmentRequestId;
        this.AllocationId = record.AllocationId;
        this.ReservationId = record.ReservationId;
        this.PropertyId = record.PropertyId;
        this.RequestFingerprint = record.RequestFingerprint;
        this.Confirmed = record.Confirmed;
        this.RejectionReason = record.RejectionReason;
        this.AllocationVersion = record.AllocationVersion;
        this.DecidedAtUtc = record.DecidedAtUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid AllocationId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public bool Confirmed { get; private set; }
    public InventoryAllocationRejectionReason? RejectionReason { get; private set; }
    public long? AllocationVersion { get; private set; }
    public DateTimeOffset DecidedAtUtc { get; private set; }

    internal InventoryAllocationAmendmentDecisionRecord ToRecord() => new(
        this.Id,
        this.ScopeId,
        this.AllocationId,
        this.ReservationId,
        this.PropertyId,
        this.RequestFingerprint,
        this.Confirmed,
        this.RejectionReason,
        this.AllocationVersion,
        this.DecidedAtUtc);

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "An allocation amendment decision requires non-empty coordinates.",
                parameterName);
        }
    }

    private static bool IsCanonicalFingerprint(string? fingerprint) =>
        fingerprint is { Length: 64 } &&
        fingerprint.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
