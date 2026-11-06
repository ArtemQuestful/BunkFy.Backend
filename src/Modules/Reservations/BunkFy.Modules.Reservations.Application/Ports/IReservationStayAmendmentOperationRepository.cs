namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Domain.StayAmendments;

public interface IReservationStayAmendmentOperationRepository
{
    Task<ReservationStayAmendmentOperation?> GetAsync(
        Guid propertyId,
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<ReservationStayAmendmentOperation?> GetVisibleAsync(
        Guid propertyId,
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<ReservationStayAmendmentOperation?> GetByInventoryRequestIdAsync(
        Guid inventoryRequestId,
        CancellationToken cancellationToken);

    Task AddAsync(
        ReservationStayAmendmentOperation operation,
        CancellationToken cancellationToken);

    Task<ReservationStayAmendmentRecoveryPageRecord> ListRecoveryAsync(
        Guid propertyId,
        ReservationStayAmendmentRecoveryCursorRecord? cursor,
        int pageSize,
        CancellationToken cancellationToken);
}
