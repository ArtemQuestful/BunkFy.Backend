namespace BunkFy.Modules.Reservations.Application.Validation;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;

internal sealed class ListReservationsQueryValidator : IQueryValidator<ListReservationsQuery>
{
    public IEnumerable<string> Validate(ListReservationsQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (query.Statuses?.Any(status => status == ReservationStatus.Unknown || !Enum.IsDefined(status)) == true)
        {
            yield return "Statuses contain an invalid value.";
        }

        if (query.Statuses?.Count > Enum.GetValues<ReservationStatus>().Length - 1)
        {
            yield return "Too many statuses were requested.";
        }

        if (query.Search?.Trim().Length > ReservationsContractLimits.SearchMaxLength)
        {
            yield return "Search exceeds the supported limit.";
        }

        if (query.Order == ReservationListOrder.Unknown || !Enum.IsDefined(query.Order))
        {
            yield return "Order is invalid.";
        }

        if (query.View.HasValue &&
            (query.View == ReservationListView.Unknown || !Enum.IsDefined(query.View.Value)))
        {
            yield return "View is invalid.";
        }

        if (query.View.HasValue != query.OperatingDate.HasValue)
        {
            yield return "View and OperatingDate must be supplied together.";
        }

        if (query.View.HasValue && query.Statuses is { Count: > 0 })
        {
            yield return "View cannot be combined with explicit reservation statuses.";
        }

        if (query.View.HasValue && query.OverlapsFrom.HasValue)
        {
            yield return "View cannot be combined with an overlap window.";
        }

        if (query.OverlapsFrom.HasValue != query.OverlapsTo.HasValue)
        {
            yield return "OverlapsFrom and OverlapsTo must be supplied together.";
        }
        else if (query.OverlapsFrom.HasValue && query.OverlapsFrom >= query.OverlapsTo)
        {
            yield return "The reservation overlap window is invalid.";
        }
        else if (query.OverlapsFrom.HasValue &&
                 query.OverlapsTo!.Value.DayNumber - query.OverlapsFrom.Value.DayNumber >
                 ReservationsContractLimits.MaximumScheduleWindowDays)
        {
            yield return $"The reservation overlap window cannot exceed {ReservationsContractLimits.MaximumScheduleWindowDays} days.";
        }
    }
}
