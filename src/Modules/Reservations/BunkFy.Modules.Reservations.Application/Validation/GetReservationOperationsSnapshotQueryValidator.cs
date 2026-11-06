namespace BunkFy.Modules.Reservations.Application.Validation;

using BunkFy.Modules.Reservations.Application.Queries;
using Gma.Framework.Cqrs;

internal sealed class GetReservationOperationsSnapshotQueryValidator
    : IQueryValidator<GetReservationOperationsSnapshotQuery>
{
    public IEnumerable<string> Validate(GetReservationOperationsSnapshotQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }
    }
}
