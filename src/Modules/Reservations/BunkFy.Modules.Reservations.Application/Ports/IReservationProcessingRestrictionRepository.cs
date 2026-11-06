namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Domain.DataRights;
using Gma.Framework.Pagination;

public interface IReservationProcessingRestrictionRepository
{
    Task<ReservationProcessingRestrictionReceipt?> FindReceiptByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<ReservationProcessingRestriction?> FindByApplyApprovalAsync(
        Guid propertyId,
        Guid reservationId,
        Guid caseId,
        long approvalRevision,
        CancellationToken cancellationToken);

    Task<ReservationProcessingRestriction?> FindByReleaseApprovalAsync(
        Guid propertyId,
        Guid reservationId,
        Guid caseId,
        long approvalRevision,
        CancellationToken cancellationToken);

    Task<ReservationProcessingRestriction?> GetAsync(
        Guid propertyId,
        Guid restrictionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ReservationProcessingRestriction>> ListActiveAsync(
        Guid propertyId,
        Guid reservationId,
        PageRequest pageRequest,
        CancellationToken cancellationToken);

    Task AddAsync(
        ReservationProcessingRestriction restriction,
        CancellationToken cancellationToken);

    Task AddReceiptAsync(
        ReservationProcessingRestrictionReceipt receipt,
        CancellationToken cancellationToken);
}
