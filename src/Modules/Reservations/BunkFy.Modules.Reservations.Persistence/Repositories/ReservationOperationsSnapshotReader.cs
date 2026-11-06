namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using System.Data;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

internal sealed class ReservationOperationsSnapshotReader(
    ReservationsDbContext dbContext)
    : IReservationOperationsSnapshotReader
{
    private static readonly ReservationOperationsCountDto EmptyCount = new(0, 0);

    public async Task<ReservationOperationsSnapshotReadResult> ReadAsync(
        Guid propertyId,
        DateOnly? explicitLocalDate,
        DateTimeOffset observedAtUtc,
        int upcomingLimit,
        CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty ||
            upcomingLimit is < 0 or >
                ReservationsContractLimits.MaximumOperationsSnapshotUpcomingLimit)
        {
            throw new ArgumentOutOfRangeException(
                propertyId == Guid.Empty
                    ? nameof(propertyId)
                    : nameof(upcomingLimit));
        }

        DateTimeOffset observation = observedAtUtc.ToUniversalTime();
        if (observation == default)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc));
        }

        if (dbContext.Database.IsRelational() &&
            dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The Reservations operations snapshot cannot run inside an existing transaction.");
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    ResolveRelationalSnapshotIsolation(
                        dbContext.Database.ProviderName),
                    cancellationToken).ConfigureAwait(false);
            }

            PropertySnapshot? property = await dbContext.PropertyProjections
                .AsNoTracking()
                .Where(item => item.Id == propertyId)
                .Select(item => new PropertySnapshot(
                    item.IsKnown,
                    item.IsActive,
                    item.TimeZoneId,
                    item.TopologySourceVersion))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (property is null ||
                !property.IsKnown ||
                property.TopologySourceVersion <= 0)
            {
                return ReservationOperationsSnapshotReadResult.PropertyNotFound();
            }

            if (!property.IsActive)
            {
                return ReservationOperationsSnapshotReadResult.PropertyInactive();
            }

            if (!TryResolveTimeZone(property.TimeZoneId, out TimeZoneInfo? timeZone))
            {
                return ReservationOperationsSnapshotReadResult.PropertyTimeZoneUnavailable();
            }

            DateOnly localDate = explicitLocalDate ?? DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(observation, timeZone).DateTime);

            IQueryable<Reservation> ordinary = ReservationVisibilityQueries
                .Ordinary(dbContext)
                .AsNoTracking()
                .Where(reservation => reservation.PropertyId == propertyId);
            ReservationOperationsAggregate? counts = await ordinary
                .Where(reservation =>
                    reservation.Status == ReservationState.PendingAllocation ||
                    reservation.Status == ReservationState.Confirmed ||
                    reservation.Status == ReservationState.AllocationRejected ||
                    reservation.Status == ReservationState.CancellationPending ||
                    reservation.Status == ReservationState.CheckedIn ||
                    reservation.Status == ReservationState.NoShowPending ||
                    reservation.Status == ReservationState.CheckoutPending)
                .GroupBy(_ => 1)
                .Select(group => new ReservationOperationsAggregate(
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.Confirmed &&
                        reservation.Arrival == localDate),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.Confirmed &&
                            reservation.Arrival == localDate)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        (reservation.Status == ReservationState.CheckedIn ||
                         reservation.Status == ReservationState.CheckoutPending) &&
                        reservation.Departure == localDate),
                    group.Where(reservation =>
                            (reservation.Status == ReservationState.CheckedIn ||
                             reservation.Status == ReservationState.CheckoutPending) &&
                            reservation.Departure == localDate)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.CheckedIn ||
                        reservation.Status == ReservationState.CheckoutPending),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.CheckedIn ||
                            reservation.Status == ReservationState.CheckoutPending)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.PendingAllocation),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.PendingAllocation)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.AllocationRejected),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.AllocationRejected)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.CancellationPending),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.CancellationPending)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.NoShowPending),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.NoShowPending)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.CheckoutPending),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.CheckoutPending)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.Confirmed &&
                        reservation.Arrival < localDate),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.Confirmed &&
                            reservation.Arrival < localDate)
                        .Sum(reservation => (long)reservation.GuestCount),
                    group.LongCount(reservation =>
                        reservation.Status == ReservationState.CheckedIn &&
                        reservation.Departure < localDate),
                    group.Where(reservation =>
                            reservation.Status == ReservationState.CheckedIn &&
                            reservation.Departure < localDate)
                        .Sum(reservation => (long)reservation.GuestCount)))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            UpcomingReservationRow[] upcomingRows = await ordinary
                .Where(reservation =>
                    (reservation.Status == ReservationState.PendingAllocation ||
                     reservation.Status == ReservationState.Confirmed) &&
                    reservation.Arrival >= localDate)
                .OrderBy(reservation => reservation.Arrival)
                .ThenBy(reservation => reservation.ExpectedArrivalTime == null)
                .ThenBy(reservation => reservation.ExpectedArrivalTime)
                .ThenBy(reservation => reservation.Id)
                .Take(upcomingLimit + 1)
                .Select(reservation => new UpcomingReservationRow(
                    reservation.Id,
                    reservation.PropertyId,
                    reservation.Arrival,
                    reservation.Departure,
                    reservation.ExpectedArrivalTime,
                    reservation.ExpectedDepartureTime,
                    reservation.PrimaryGuestName,
                    reservation.GuestCount,
                    reservation.RequestedUnits.Select(unit => unit.Id).ToArray(),
                    reservation.AllocationId,
                    reservation.Source,
                    reservation.Status))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            bool hasMore = upcomingRows.Length > upcomingLimit;
            ReservationOperationsSnapshotDto snapshot = new(
                propertyId,
                localDate,
                timeZone.Id,
                explicitLocalDate.HasValue
                    ? ReservationOperationsDateSource.Explicit
                    : ReservationOperationsDateSource.PropertyTimeZone,
                observation,
                MapCohorts(counts),
                MapAttention(counts),
                upcomingRows
                    .Take(upcomingLimit)
                    .Select(MapUpcoming)
                    .ToArray(),
                upcomingLimit,
                hasMore);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return ReservationOperationsSnapshotReadResult.Found(snapshot);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool TryResolveTimeZone(
        string? timeZoneId,
        out TimeZoneInfo timeZone)
    {
        timeZone = null!;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo candidate = TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId.Trim());
            if (!candidate.HasIanaId)
            {
                return false;
            }

            timeZone = candidate;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    internal static IsolationLevel ResolveRelationalSnapshotIsolation(
        string? providerName) => providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                IsolationLevel.RepeatableRead,
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                IsolationLevel.Serializable,
            _ => throw new InvalidOperationException(
                $"The Reservations operations snapshot does not support relational provider '{providerName ?? "unknown"}'.")
        };

    private static ReservationOperationsCohortCountsDto MapCohorts(
        ReservationOperationsAggregate? counts) =>
        counts is null
            ? new(EmptyCount, EmptyCount, EmptyCount)
            : new(
                new(counts.ConfirmedArrivalsReservations,
                    counts.ConfirmedArrivalsGuests),
                new(counts.ScheduledDeparturesReservations,
                    counts.ScheduledDeparturesGuests),
                new(counts.InHouseReservations, counts.InHouseGuests));

    private static ReservationOperationsAttentionCountsDto MapAttention(
        ReservationOperationsAggregate? counts)
    {
        if (counts is null)
        {
            return new(
                EmptyCount,
                EmptyCount,
                EmptyCount,
                EmptyCount,
                EmptyCount,
                EmptyCount,
                EmptyCount,
                EmptyCount);
        }

        ReservationOperationsCountDto pendingAllocation = new(
            counts.PendingAllocationReservations,
            counts.PendingAllocationGuests);
        ReservationOperationsCountDto allocationRejected = new(
            counts.AllocationRejectedReservations,
            counts.AllocationRejectedGuests);
        ReservationOperationsCountDto cancellationPending = new(
            counts.CancellationPendingReservations,
            counts.CancellationPendingGuests);
        ReservationOperationsCountDto noShowPending = new(
            counts.NoShowPendingReservations,
            counts.NoShowPendingGuests);
        ReservationOperationsCountDto checkoutPending = new(
            counts.CheckoutPendingReservations,
            counts.CheckoutPendingGuests);
        ReservationOperationsCountDto arrivalBeforeLocalDateStillConfirmed = new(
            counts.ArrivalBeforeLocalDateStillConfirmedReservations,
            counts.ArrivalBeforeLocalDateStillConfirmedGuests);
        ReservationOperationsCountDto departureBeforeLocalDateStillInHouse = new(
            counts.DepartureBeforeLocalDateStillInHouseReservations,
            counts.DepartureBeforeLocalDateStillInHouseGuests);
        return new(
            pendingAllocation,
            allocationRejected,
            cancellationPending,
            noShowPending,
            checkoutPending,
            arrivalBeforeLocalDateStillConfirmed,
            departureBeforeLocalDateStillInHouse,
            new(
                pendingAllocation.ReservationCount +
                allocationRejected.ReservationCount +
                cancellationPending.ReservationCount +
                noShowPending.ReservationCount +
                checkoutPending.ReservationCount +
                arrivalBeforeLocalDateStillConfirmed.ReservationCount +
                departureBeforeLocalDateStillInHouse.ReservationCount,
                pendingAllocation.GuestCount +
                allocationRejected.GuestCount +
                cancellationPending.GuestCount +
                noShowPending.GuestCount +
                checkoutPending.GuestCount +
                arrivalBeforeLocalDateStillConfirmed.GuestCount +
                departureBeforeLocalDateStillInHouse.GuestCount));
    }

    private static ReservationListItemDto MapUpcoming(
        UpcomingReservationRow reservation) => new(
        reservation.ReservationId,
        reservation.PropertyId,
        reservation.Arrival,
        reservation.Departure,
        reservation.ExpectedArrivalTime,
        reservation.ExpectedDepartureTime,
        reservation.PrimaryGuestName,
        reservation.GuestCount,
        reservation.InventoryUnitIds.Length,
        reservation.InventoryUnitIds,
        ReservationInventoryTruthPolicy.HoldsInventory(
            reservation.Status,
            reservation.AllocationId),
        reservation.Source == ReservationSource.Direct
            ? ReservationSourceKind.Direct
            : ReservationSourceKind.External,
        reservation.Status switch
        {
            ReservationState.PendingAllocation => ReservationStatus.PendingAllocation,
            ReservationState.Confirmed => ReservationStatus.Confirmed,
            _ => ReservationStatus.Unknown
        });

    private sealed record PropertySnapshot(
        bool IsKnown,
        bool IsActive,
        string? TimeZoneId,
        long TopologySourceVersion);

    private sealed record ReservationOperationsAggregate(
        long ConfirmedArrivalsReservations,
        long ConfirmedArrivalsGuests,
        long ScheduledDeparturesReservations,
        long ScheduledDeparturesGuests,
        long InHouseReservations,
        long InHouseGuests,
        long PendingAllocationReservations,
        long PendingAllocationGuests,
        long AllocationRejectedReservations,
        long AllocationRejectedGuests,
        long CancellationPendingReservations,
        long CancellationPendingGuests,
        long NoShowPendingReservations,
        long NoShowPendingGuests,
        long CheckoutPendingReservations,
        long CheckoutPendingGuests,
        long ArrivalBeforeLocalDateStillConfirmedReservations,
        long ArrivalBeforeLocalDateStillConfirmedGuests,
        long DepartureBeforeLocalDateStillInHouseReservations,
        long DepartureBeforeLocalDateStillInHouseGuests);

    private sealed record UpcomingReservationRow(
        Guid ReservationId,
        Guid PropertyId,
        DateOnly Arrival,
        DateOnly Departure,
        TimeOnly? ExpectedArrivalTime,
        TimeOnly? ExpectedDepartureTime,
        string PrimaryGuestName,
        int GuestCount,
        Guid[] InventoryUnitIds,
        Guid? AllocationId,
        ReservationSource Source,
        ReservationState Status);
}
