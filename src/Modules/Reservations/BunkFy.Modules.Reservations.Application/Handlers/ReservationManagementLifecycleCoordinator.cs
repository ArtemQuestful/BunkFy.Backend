namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Stations;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ReservationManagementLifecycleCoordinator(
    ReservationMutationCoordinator mutations,
    IReservationManagementOperationRepository operations,
    ISystemClock clock)
{
    public async Task<Result<ReservationMutationReceiptDto>> ExecuteAsync(
        Guid operationId,
        Guid propertyId,
        Guid reservationId,
        ReservationManagementOperationKind kind,
        long expectedVersion,
        DateOnly? businessDate,
        string? actorId,
        Func<Reservation, DateTimeOffset, Result> apply,
        CancellationToken cancellationToken,
        StationCheckInProvenance? station = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!IsValidRequest(operationId, kind, businessDate, actorId) ||
            (station is not null && (!StationOperationProvenance.ValidNew(station) || kind is not (ReservationManagementOperationKind.CheckIn or ReservationManagementOperationKind.CheckOut))))
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ManagementOperationInvalid);
        }

        Reservation? reservation = await mutations.AcquireOperationalAsync(
            propertyId,
            reservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null || reservation.PropertyId != propertyId)
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ReservationNotFound);
        }

        ReservationManagementOperationRecord? existing = await operations
            .GetAsync(reservationId, operationId, cancellationToken)
            .ConfigureAwait(false);
        ReservationStationAttributionRead attribution = await operations.GetStationAttributionAsync(
            reservationId, operationId, cancellationToken).ConfigureAwait(false);
        if (station is not null && !attribution.Supported)
        { return Result.Failure<ReservationMutationReceiptDto>(ReservationsApplicationErrors.ManagementOperationInvalid); }
        if (existing is not null)
        {
            return existing.PropertyId == reservation.PropertyId &&
                existing.MatchesLifecycle(kind, expectedVersion, businessDate) && StationOperationProvenance.Matches(kind, attribution.Provenance, station)
                ? Result.Success(reservation.ToMutationReceipt())
                : Result.Failure<ReservationMutationReceiptDto>(
                    ReservationsApplicationErrors.ManagementOperationConflict);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result applied = apply(reservation, nowUtc);
        if (applied.IsFailure)
        {
            return Result.Failure<ReservationMutationReceiptDto>(applied.Error);
        }

        var operation = new ReservationManagementOperationRecord(
                operationId,
                reservation.ScopeId,
                propertyId,
                reservationId,
                kind,
                expectedVersion,
                ExpectedDetailsRevision: null,
                businessDate,
                nowUtc);
        if (station is null)
        { await operations.AddAsync(operation, cancellationToken).ConfigureAwait(false); }
        else
        { await operations.AddStationAsync(operation, station, reservation.Version, cancellationToken).ConfigureAwait(false); }
        return Result.Success(reservation.ToMutationReceipt());
    }

    private static bool IsValidRequest(
        Guid operationId,
        ReservationManagementOperationKind kind,
        DateOnly? businessDate,
        string? actorId)
    {
        bool isCancellation = kind == ReservationManagementOperationKind.Cancel;
        if (operationId == Guid.Empty ||
            kind == ReservationManagementOperationKind.Unknown ||
            !Enum.IsDefined(kind) ||
            isCancellation == businessDate.HasValue)
        {
            return false;
        }

        string normalizedActor = actorId?.Trim() ?? string.Empty;
        return isCancellation
            ? normalizedActor.Length <= Reservation.ActorIdMaxLength
            : normalizedActor.Length is > 0 and <= Reservation.ActorIdMaxLength;
    }
}
