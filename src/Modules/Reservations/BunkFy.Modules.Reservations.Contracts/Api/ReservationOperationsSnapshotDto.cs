namespace BunkFy.Modules.Reservations.Contracts;

public enum ReservationOperationsDateSource
{
    Unknown = 0,
    PropertyTimeZone = 1,
    Explicit = 2
}

public sealed record ReservationOperationsCountDto(
    long ReservationCount,
    long GuestCount);

public sealed record ReservationOperationsCohortCountsDto(
    ReservationOperationsCountDto ConfirmedArrivalsOnLocalDate,
    ReservationOperationsCountDto ScheduledDeparturesOnLocalDate,
    ReservationOperationsCountDto CurrentlyInHouse);

public sealed record ReservationOperationsAttentionCountsDto(
    ReservationOperationsCountDto PendingAllocation,
    ReservationOperationsCountDto AllocationRejected,
    ReservationOperationsCountDto CancellationPending,
    ReservationOperationsCountDto NoShowPending,
    ReservationOperationsCountDto CheckoutPending,
    ReservationOperationsCountDto ArrivalBeforeLocalDateStillConfirmed,
    ReservationOperationsCountDto DepartureBeforeLocalDateStillInHouse,
    ReservationOperationsCountDto Total);

public sealed record ReservationOperationsSnapshotDto(
    Guid PropertyId,
    DateOnly LocalDate,
    string TimeZoneId,
    ReservationOperationsDateSource DateSource,
    DateTimeOffset ObservedAtUtc,
    ReservationOperationsCohortCountsDto Cohorts,
    ReservationOperationsAttentionCountsDto Attention,
    IReadOnlyCollection<ReservationListItemDto> Upcoming,
    int UpcomingLimit,
    bool HasMoreUpcoming);
