namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Domain;

internal sealed class ReservationStationAttribution : IScopedEntity
{
    private ReservationStationAttribution() { }
    internal ReservationStationAttribution(string scopeId, Guid reservationId, Guid operationId,
        StationCheckInProvenance provenance, long resultingVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        if (reservationId == Guid.Empty || operationId == Guid.Empty || !provenance.IsValid || resultingVersion < 1)
        { throw new ArgumentException("Invalid station attribution."); }
        this.ScopeId = scopeId;
        this.ReservationId = reservationId;
        this.OperationId = operationId;
        this.StationId = provenance.StationId;
        this.BrowserSessionId = provenance.BrowserSessionId;
        this.StaffMemberId = provenance.StaffMemberId;
        this.ActorSessionId = provenance.ActorSessionId;
        this.Generation = provenance.Generation;
        this.Authority = provenance.Authority;
        this.ResultingVersion = resultingVersion;
        this.JobGrantRevision = provenance.JobGrantRevision;
    }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid ReservationId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid StationId { get; private set; }
    public Guid BrowserSessionId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public Guid ActorSessionId { get; private set; }
    public long Generation { get; private set; }
    public StationReservationAuthority Authority { get; private set; }
    public long? JobGrantRevision { get; private set; }
    public long ResultingVersion { get; private set; }
    internal StationCheckInProvenance ToProvenance() => new(this.StationId, this.BrowserSessionId,
        this.StaffMemberId, this.ActorSessionId, this.Generation, this.Authority, this.JobGrantRevision);
}
