namespace BunkFy.Modules.Reservations.Application.Validation;

using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

internal sealed class ListReservationStayAmendmentRecoveryQueryValidator
    : IQueryValidator<ListReservationStayAmendmentRecoveryQuery>
{
    public const int MaximumPageSize = 100;

    public IEnumerable<string> Validate(ListReservationStayAmendmentRecoveryQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (query.PageSize is < 1 or > MaximumPageSize)
        {
            yield return $"PageSize must be between 1 and {MaximumPageSize}.";
        }

        if (query.Cursor is not null &&
            (query.Cursor.Outcome is not (
                    ReservationStayAmendmentOutcome.Pending or
                    ReservationStayAmendmentOutcome.OutcomeUnknown) ||
                query.Cursor.UpdatedAtUtc == default ||
                query.Cursor.OperationId == Guid.Empty ||
                query.Cursor.ReservationId == Guid.Empty))
        {
            yield return "Cursor is invalid.";
        }
    }
}
