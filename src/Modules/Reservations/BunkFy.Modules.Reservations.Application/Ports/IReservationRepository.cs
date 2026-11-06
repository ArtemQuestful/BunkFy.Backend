namespace BunkFy.Modules.Reservations.Application.Ports;

using Gma.Framework.Pagination;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;

public interface IReservationRepository
{
    Task AddAsync(Reservation reservation, CancellationToken cancellationToken);
    Task AddUnderAcquiredOperationLockAsync(
        Reservation reservation,
        CancellationToken cancellationToken) =>
        this.AddAsync(reservation, cancellationToken);
    Task<Reservation?> GetAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken);
    async Task<bool> ExistsAsync(
        Guid propertyId,
        Guid reservationId,
        CancellationToken cancellationToken) =>
        await this.GetAsync(propertyId, reservationId, cancellationToken)
            .ConfigureAwait(false) is not null;
    Task<Reservation?> GetForDataRightsAsync(
        Guid propertyId,
        Guid reservationId,
        CancellationToken cancellationToken);
    Task<Reservation?> GetAsyncByReservationId(
        Guid reservationId,
        CancellationToken cancellationToken);
    Task<Reservation?> GetForRequiredContinuationAsync(
        Guid propertyId,
        Guid reservationId,
        CancellationToken cancellationToken) =>
        this.GetAsync(propertyId, reservationId, cancellationToken);
    Task<Reservation?> GetForRequiredContinuationByReservationIdAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        this.GetAsyncByReservationId(reservationId, cancellationToken);
    Task<Reservation?> GetForCreationReplayByReservationIdAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        this.GetForRequiredContinuationByReservationIdAsync(
            reservationId,
            cancellationToken);
    Task<Reservation?> GetByExternalSourceAsync(string sourceSystem, string sourceReference, CancellationToken cancellationToken);
    Task<bool> ExternalSourceExistsAsync(string sourceSystem, string sourceReference, CancellationToken cancellationToken);
    Task<ReservationListResponse> ListAsync(
        Guid propertyId,
        IReadOnlyCollection<ReservationStatus>? statuses,
        string? search,
        ReservationListOrder order,
        PageRequest pageRequest,
        CancellationToken cancellationToken);

    Task<ReservationListResponse> ListWindowAsync(
        Guid propertyId,
        IReadOnlyCollection<ReservationStatus>? statuses,
        string? search,
        ReservationListOrder order,
        PageRequest pageRequest,
        DateOnly? overlapsFrom,
        DateOnly? overlapsTo,
        ReservationListView? view,
        DateOnly? operatingDate,
        CancellationToken cancellationToken) =>
        this.ListAsync(
            propertyId,
            statuses,
            search,
            order,
            pageRequest,
            cancellationToken);
}
