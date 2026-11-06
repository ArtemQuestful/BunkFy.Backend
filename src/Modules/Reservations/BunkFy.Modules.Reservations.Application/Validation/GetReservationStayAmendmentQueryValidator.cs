namespace BunkFy.Modules.Reservations.Application.Validation;

using BunkFy.Modules.Reservations.Application.Queries;
using Gma.Framework.Cqrs;

internal sealed class GetReservationStayAmendmentQueryValidator
    : IQueryValidator<GetReservationStayAmendmentQuery>
{
    public IEnumerable<string> Validate(GetReservationStayAmendmentQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (query.ReservationId == Guid.Empty)
        {
            yield return "ReservationId is required.";
        }

        if (query.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }
    }
}
