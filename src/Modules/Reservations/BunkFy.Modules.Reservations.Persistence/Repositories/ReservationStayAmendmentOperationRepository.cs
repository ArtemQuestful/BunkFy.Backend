namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class ReservationStayAmendmentOperationRepository(
    ReservationsDbContext dbContext)
    : IReservationStayAmendmentOperationRepository
{
    public Task<ReservationStayAmendmentOperation?> GetAsync(
        Guid propertyId,
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken) =>
        dbContext.StayAmendmentOperations.SingleOrDefaultAsync(
            operation =>
                operation.PropertyId == propertyId &&
                operation.ReservationId == reservationId &&
                operation.Id == operationId,
            cancellationToken);

    public Task<ReservationStayAmendmentOperation?> GetVisibleAsync(
        Guid propertyId,
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken) =>
        BuildVisibleQuery(
                dbContext,
                propertyId,
                reservationId,
                operationId)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ReservationStayAmendmentOperation?> GetByInventoryRequestIdAsync(
        Guid inventoryRequestId,
        CancellationToken cancellationToken) =>
        dbContext.StayAmendmentOperations.SingleOrDefaultAsync(
            operation => operation.InventoryRequestId == inventoryRequestId,
            cancellationToken);

    internal static IQueryable<ReservationStayAmendmentOperation>
        BuildVisibleQuery(
            ReservationsDbContext dbContext,
            Guid propertyId,
            Guid reservationId,
            Guid operationId)
    {
        IQueryable<Reservation> ordinaryReservations =
            ReservationVisibilityQueries.Ordinary(dbContext);
        return dbContext.StayAmendmentOperations
            .AsNoTracking()
            .Where(operation =>
                    operation.PropertyId == propertyId &&
                    operation.ReservationId == reservationId &&
                    operation.Id == operationId &&
                    ordinaryReservations.Any(reservation =>
                        reservation.PropertyId == operation.PropertyId &&
                        reservation.Id == operation.ReservationId));
    }

    public Task AddAsync(
        ReservationStayAmendmentOperation operation,
        CancellationToken cancellationToken)
    {
        dbContext.StayAmendmentOperations.Add(operation);
        return Task.CompletedTask;
    }

    public async Task<ReservationStayAmendmentRecoveryPageRecord>
        ListRecoveryAsync(
            Guid propertyId,
            ReservationStayAmendmentRecoveryCursorRecord? cursor,
            int pageSize,
            CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "A property identifier is required.",
                nameof(propertyId));
        }

        if (pageSize is < 1 or > PageRequest.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (cursor is not null &&
            (cursor.Outcome is not (
                 ReservationStayAmendmentOperationOutcome.Pending or
                 ReservationStayAmendmentOperationOutcome.OutcomeUnknown) ||
             cursor.UpdatedAtUtc == default ||
             cursor.OperationId == Guid.Empty ||
             cursor.ReservationId == Guid.Empty))
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        ReservationStayAmendmentOperation[] lookahead = await BuildRecoveryQuery(
                dbContext,
                propertyId,
                cursor,
                pageSize)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        ReservationStayAmendmentOperation[] operations = lookahead
            .Take(pageSize)
            .ToArray();
        ReservationStayAmendmentRecoveryCursorRecord? nextCursor =
            lookahead.Length > pageSize
                ? ToCursor(operations[^1])
                : null;
        return new(operations, nextCursor);
    }

    internal static IQueryable<ReservationStayAmendmentOperation>
        BuildRecoveryQuery(
            ReservationsDbContext dbContext,
            Guid propertyId,
            ReservationStayAmendmentRecoveryCursorRecord? cursor,
            int pageSize)
    {
        IQueryable<Reservation> ordinaryReservations =
            ReservationVisibilityQueries.Ordinary(dbContext);
        IQueryable<ReservationStayAmendmentOperation> query = dbContext
            .StayAmendmentOperations
            .AsNoTracking()
            .Where(operation =>
                operation.PropertyId == propertyId &&
                (operation.Outcome ==
                    ReservationStayAmendmentOperationOutcome.Pending ||
                 operation.Outcome ==
                    ReservationStayAmendmentOperationOutcome.OutcomeUnknown) &&
                ordinaryReservations.Any(reservation =>
                    reservation.PropertyId == operation.PropertyId &&
                    reservation.Id == operation.ReservationId));

        if (cursor?.Outcome ==
                ReservationStayAmendmentOperationOutcome.Pending)
        {
            query = query.Where(operation =>
                operation.Outcome ==
                    ReservationStayAmendmentOperationOutcome.OutcomeUnknown ||
                (operation.Outcome ==
                    ReservationStayAmendmentOperationOutcome.Pending &&
                 (operation.UpdatedAtUtc > cursor.UpdatedAtUtc ||
                  (operation.UpdatedAtUtc == cursor.UpdatedAtUtc &&
                   (operation.Id.CompareTo(cursor.OperationId) > 0 ||
                    (operation.Id == cursor.OperationId &&
                     operation.ReservationId.CompareTo(
                         cursor.ReservationId) > 0))))));
        }
        else if (cursor?.Outcome ==
                 ReservationStayAmendmentOperationOutcome.OutcomeUnknown)
        {
            query = query.Where(operation =>
                operation.Outcome ==
                    ReservationStayAmendmentOperationOutcome.OutcomeUnknown &&
                (operation.UpdatedAtUtc > cursor.UpdatedAtUtc ||
                 (operation.UpdatedAtUtc == cursor.UpdatedAtUtc &&
                  (operation.Id.CompareTo(cursor.OperationId) > 0 ||
                   (operation.Id == cursor.OperationId &&
                    operation.ReservationId.CompareTo(
                        cursor.ReservationId) > 0)))));
        }

        return query
            .OrderBy(operation => operation.Outcome)
            .ThenBy(operation => operation.UpdatedAtUtc)
            .ThenBy(operation => operation.Id)
            .ThenBy(operation => operation.ReservationId)
            .Take(checked(pageSize + 1));
    }

    private static ReservationStayAmendmentRecoveryCursorRecord ToCursor(
        ReservationStayAmendmentOperation operation) => new(
            operation.Outcome,
            operation.UpdatedAtUtc,
            operation.Id,
            operation.ReservationId);
}
