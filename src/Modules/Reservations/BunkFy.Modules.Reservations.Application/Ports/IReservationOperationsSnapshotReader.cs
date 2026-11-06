namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Contracts;

public interface IReservationOperationsSnapshotReader
{
    Task<ReservationOperationsSnapshotReadResult> ReadAsync(
        Guid propertyId,
        DateOnly? explicitLocalDate,
        DateTimeOffset observedAtUtc,
        int upcomingLimit,
        CancellationToken cancellationToken);
}

public enum ReservationOperationsSnapshotReadStatus
{
    Unknown = 0,
    Found = 1,
    PropertyNotFound = 2,
    PropertyInactive = 3,
    PropertyTimeZoneUnavailable = 4
}

public sealed record ReservationOperationsSnapshotReadResult(
    ReservationOperationsSnapshotReadStatus Status,
    ReservationOperationsSnapshotDto? Snapshot)
{
    public static ReservationOperationsSnapshotReadResult Found(
        ReservationOperationsSnapshotDto snapshot) =>
        new(ReservationOperationsSnapshotReadStatus.Found, snapshot);

    public static ReservationOperationsSnapshotReadResult PropertyNotFound() =>
        new(ReservationOperationsSnapshotReadStatus.PropertyNotFound, null);

    public static ReservationOperationsSnapshotReadResult PropertyInactive() =>
        new(ReservationOperationsSnapshotReadStatus.PropertyInactive, null);

    public static ReservationOperationsSnapshotReadResult PropertyTimeZoneUnavailable() =>
        new(ReservationOperationsSnapshotReadStatus.PropertyTimeZoneUnavailable, null);
}
