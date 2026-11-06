namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Domain;

public sealed class ReservationExternalOperation : IScopedEntity
{
    public const string RedactedRequestFingerprint =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private ReservationExternalOperation() { }

    internal ReservationExternalOperation(ReservationExternalOperationRecord record)
    {
        this.Id = record.OperationId;
        this.ScopeId = record.ScopeId;
        this.ReceiptId = record.ReceiptId;
        this.ConnectionId = record.ConnectionId;
        this.PropertyId = record.PropertyId;
        this.Kind = record.Kind;
        this.RequestFingerprint = record.RequestFingerprint;
        this.Outcome = record.Outcome;
        this.ReservationId = record.ReservationId;
        this.DetailsRevision = record.DetailsRevision;
        this.ReservationVersion = record.ReservationVersion;
        this.ErrorCode = record.ErrorCode;
        this.CompletedAtUtc = record.CompletedAtUtc;
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid ReceiptId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid PropertyId { get; private set; }
    public ExternalReservationOperationKind Kind { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public ExternalReservationOperationOutcome Outcome { get; private set; }
    public Guid? ReservationId { get; private set; }
    public long? DetailsRevision { get; private set; }
    public long? ReservationVersion { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }

    internal ReservationExternalOperationRecord ToRecord() => new(
        this.Id,
        this.ScopeId,
        this.ReceiptId,
        this.ConnectionId,
        this.PropertyId,
        this.Kind,
        this.RequestFingerprint,
        this.Outcome,
        this.ReservationId,
        this.DetailsRevision,
        this.ReservationVersion,
        this.ErrorCode,
        this.CompletedAtUtc);

    internal void ReduceToReconciliationProof()
    {
        this.RequestFingerprint = RedactedRequestFingerprint;
        this.ErrorCode = null;
    }
}
