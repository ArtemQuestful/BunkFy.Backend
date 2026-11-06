namespace BunkFy.Modules.Reservations.Application.Queries;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Reservations.Contracts;

public sealed record ListReservationsQuery(
    Guid PropertyId,
    IReadOnlyCollection<ReservationStatus>? Statuses,
    string? Search,
    ReservationListOrder Order,
    int Page,
    int PageSize,
    DateOnly? OverlapsFrom = null,
    DateOnly? OverlapsTo = null,
    ReservationListView? View = null,
    DateOnly? OperatingDate = null)
    : IQuery<ReservationListResponse>;
