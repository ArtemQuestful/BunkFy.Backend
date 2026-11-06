namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Domain;

public sealed class ReservationDetailsHistoryEntry : IScopedEntity
{
    private ReservationDetailsHistoryEntry() { }

    internal ReservationDetailsHistoryEntry(
        Guid id,
        string scopeId,
        Guid reservationId,
        Guid propertyId,
        long fromRevision,
        long toRevision,
        ReservationDetailsChangeOrigin origin,
        string? actorId,
        Guid? adapterConnectionId,
        Guid? externalOperationId,
        string operationDeduplicationKey,
        Guid correlationId,
        string changedFieldsJson,
        string? beforeSnapshotJson,
        string afterSnapshotJson,
        string afterSnapshotHash,
        DateTimeOffset occurredAtUtc)
    {
        this.Id = id;
        this.ScopeId = scopeId;
        this.ReservationId = reservationId;
        this.PropertyId = propertyId;
        this.FromRevision = fromRevision;
        this.ToRevision = toRevision;
        this.Origin = origin;
        this.ActorId = actorId;
        this.AdapterConnectionId = adapterConnectionId;
        this.ExternalOperationId = externalOperationId;
        this.OperationDeduplicationKey = operationDeduplicationKey;
        this.CorrelationId = correlationId;
        this.ChangedFieldsJson = changedFieldsJson;
        this.BeforeSnapshotJson = beforeSnapshotJson;
        this.AfterSnapshotJson = afterSnapshotJson;
        this.AfterSnapshotHash = afterSnapshotHash;
        this.OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public long FromRevision { get; private set; }
    public long ToRevision { get; private set; }
    public ReservationDetailsChangeOrigin Origin { get; private set; }
    public string? ActorId { get; private set; }
    public Guid? AdapterConnectionId { get; private set; }
    public Guid? ExternalOperationId { get; private set; }
    public string OperationDeduplicationKey { get; private set; } = string.Empty;
    public Guid CorrelationId { get; private set; }
    public string ChangedFieldsJson { get; private set; } = string.Empty;
    public string? BeforeSnapshotJson { get; private set; }
    public string AfterSnapshotJson { get; private set; } = string.Empty;
    public string AfterSnapshotHash { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    internal void RedactSnapshots(
        string? beforeSnapshotJson,
        string afterSnapshotJson,
        string afterSnapshotHash)
    {
        this.BeforeSnapshotJson = beforeSnapshotJson;
        this.AfterSnapshotJson = afterSnapshotJson;
        this.AfterSnapshotHash = afterSnapshotHash;
    }
}
