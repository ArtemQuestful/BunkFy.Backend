namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Reservations.Application.Ports;
using Gma.Framework.Domain;

internal sealed class ReservationManagementOperation : IScopedEntity
{
    private ReservationManagementOperation() { }

    internal ReservationManagementOperation(ReservationManagementOperationRecord record)
    {
        this.Id = record.OperationId;
        this.ScopeId = record.ScopeId;
        this.PropertyId = record.PropertyId;
        this.ReservationId = record.ReservationId;
        this.Kind = record.Kind;
        this.ExpectedVersion = record.ExpectedVersion;
        this.ExpectedDetailsRevision = record.ExpectedDetailsRevision;
        this.BusinessDate = record.BusinessDate;
        this.CreatedAtUtc = record.CreatedAtUtc;
        this.RequestFingerprint = record.RequestFingerprint;
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid PropertyId { get; private set; }
    public Guid ReservationId { get; private set; }
    public ReservationManagementOperationKind Kind { get; private set; }
    public long? ExpectedVersion { get; private set; }
    public long? ExpectedDetailsRevision { get; private set; }
    public DateOnly? BusinessDate { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? RequestFingerprint { get; private set; }

    internal ReservationManagementOperationRecord ToRecord() => new(
        this.Id,
        this.ScopeId,
        this.PropertyId,
        this.ReservationId,
        this.Kind,
        this.ExpectedVersion,
        this.ExpectedDetailsRevision,
        this.BusinessDate,
        this.CreatedAtUtc,
        this.RequestFingerprint);
}
