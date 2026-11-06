namespace BunkFy.Modules.Reservations.Application.Validation;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class ReconcileReservationStayAmendmentCommandValidator
    : ICommandValidator<ReconcileReservationStayAmendmentCommand>
{
    public IEnumerable<string> Validate(ReconcileReservationStayAmendmentCommand command)
    {
        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (command.ReservationId == Guid.Empty)
        {
            yield return "ReservationId is required.";
        }

        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.ExpectedOperationVersion <= 0)
        {
            yield return "ExpectedOperationVersion must be greater than zero.";
        }

        if (string.IsNullOrWhiteSpace(command.ActorId) ||
            command.ActorId.Trim().Length > Reservation.ActorIdMaxLength ||
            command.ActorId.Any(char.IsControl))
        {
            yield return "ActorId is required, bounded, and control-character free.";
        }
    }
}
