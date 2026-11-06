namespace BunkFy.Modules.Reservations.Contracts.Stations;

/// <summary>Reservations-owned provenance; never an Auth principal or reusable authority grant.</summary>
public enum StationReservationAuthority { Unknown = 0, LinkedStation = 1, StationOnly = 2 }
public enum StationReservationState { Ready = 0, Denied = 1, Unavailable = 2, Unsupported = 3, Incomplete = 4, Conflict = 5, Applied = 6 }
public sealed record StationCheckInProvenance(Guid StationId, Guid BrowserSessionId, Guid StaffMemberId,
    Guid ActorSessionId, long Generation, StationReservationAuthority Authority, long? JobGrantRevision = null)
{
    public bool IsValid => this.StationId != Guid.Empty && this.BrowserSessionId != Guid.Empty &&
        this.StaffMemberId != Guid.Empty && this.ActorSessionId != Guid.Empty && this.Generation > 0 &&
        this.Authority is StationReservationAuthority.LinkedStation or StationReservationAuthority.StationOnly &&
        (this.JobGrantRevision is null or > 0) && (this.Authority != StationReservationAuthority.LinkedStation || this.JobGrantRevision is null);
}
public sealed record StationArrivalCursor(DateOnly Arrival, string NormalizedGuestName, Guid ReservationId);
public sealed record StationAllocationUnit(Guid InventoryUnitId, Guid RoomId, Guid? BedId, int Kind,
    long ConfigurationVersion, long UnitVersion);
public sealed record StationDueArrival(Guid ReservationId, string PrimaryGuestName, DateOnly Arrival, DateOnly Departure,
    long ExpectedVersion, Guid AllocationId, long AllocationVersion, IReadOnlyList<StationAllocationUnit> Units);
public sealed record StationDueArrivalPage(StationReservationState State, IReadOnlyList<StationDueArrival> Items,
    StationArrivalCursor? Continuation = null);
public sealed record StationCheckInPreparation(StationReservationState State, DateOnly BusinessDate,
    StationDueArrival? Arrival = null, bool Replay = false);
public sealed record StationCheckInResult(StationReservationState State, ReservationMutationReceiptDto? Receipt = null);

/// <summary>Guest-free historical confirmation only. Pending never proves that a delayed write cannot commit.</summary>
public enum StationCheckInOutcomeState { Pending = 0, Applied = 1, Conflict = 2, Unavailable = 3 }
public sealed record StationCheckInOutcome(StationCheckInOutcomeState State);

/// <summary>Reads only persisted operation/provenance facts; cannot dispatch or retry a mutation.</summary>
public interface IStationCheckInOutcomeReader
{
    Task<StationCheckInOutcome> ResolveAsync(Guid propertyId, Guid stationId, Guid browserSessionId,
        Guid actorSessionId, long generation, Guid reservationId, Guid operationId, long expectedVersion,
        CancellationToken cancellationToken = default);
}

public enum StationDepartureState { Due = 0, ReleasePending = 1, ReleaseRejected = 2 }
public sealed record StationDepartureCursor(DateOnly Departure, string NormalizedGuestName, Guid ReservationId);
public sealed record StationDueDeparture(Guid ReservationId, string PrimaryGuestName, DateOnly Arrival, DateOnly Departure,
    DateOnly CheckedInBusinessDate, long ExpectedVersion, Guid AllocationId, long AllocationVersion,
    IReadOnlyList<StationAllocationUnit> Units, StationDepartureState State);
public sealed record StationDueDeparturePage(StationReservationState State, IReadOnlyList<StationDueDeparture> Items,
    StationDepartureCursor? Continuation = null);
public sealed record StationCheckOutPreparation(StationReservationState State, DateOnly BusinessDate,
    StationDueDeparture? Departure = null, bool Replay = false);
public enum StationCheckOutOutcomeState { Pending = 0, ReleasePending = 1, Completed = 2, ReleaseRejected = 3, Conflict = 4, Unavailable = 5, Recorded = 6 }
public sealed record StationCheckOutOutcome(StationCheckOutOutcomeState State);
public sealed record StationCheckOutResult(StationReservationState State, ReservationMutationReceiptDto? Receipt = null,
    StationCheckOutOutcomeState? Checkout = null);
public interface IStationCheckOutOutcomeReader
{
    Task<StationCheckOutOutcome> ResolveAsync(Guid propertyId, Guid stationId, Guid browserSessionId,
        Guid actorSessionId, long generation, Guid reservationId, Guid operationId, long expectedVersion,
        CancellationToken cancellationToken = default);
}
public interface IStationDepartureOperations
{
    Task<StationDueDeparturePage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationDepartureCursor? after, CancellationToken cancellationToken = default);
    Task<StationCheckOutPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
        long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default);
    Task<StationCheckOutResult> CheckOutAsync(Guid propertyId, Guid reservationId, Guid operationId, long expectedVersion,
        StationCheckOutPreparation preparation, StationCheckInProvenance provenance, CancellationToken cancellationToken = default);
}

/// <summary>Internal owner boundary. All coordinates/date/provenance are established by Stations on the server.</summary>
public interface IStationReservationOperations
{
    Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationArrivalCursor? after, CancellationToken cancellationToken = default);
    Task<StationCheckInPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
        long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default);
    Task<StationCheckInResult> CheckInAsync(Guid propertyId, Guid reservationId, Guid operationId, long expectedVersion,
        StationCheckInPreparation preparation, StationCheckInProvenance provenance, CancellationToken cancellationToken = default);
}
