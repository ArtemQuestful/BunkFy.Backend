namespace BunkFy.Modules.Reservations.Application.Validation;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class AmendReservationStayCommandValidator
    : ICommandValidator<AmendReservationStayCommand>
{
    public IEnumerable<string> Validate(AmendReservationStayCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (command.ReservationId == Guid.Empty)
        {
            yield return "ReservationId is required.";
        }

        if (command.Arrival == default || command.Departure == default || command.Arrival >= command.Departure)
        {
            yield return "Arrival must be before Departure.";
        }

        if (!HasMinutePrecision(command.ExpectedArrivalTime) ||
            !HasMinutePrecision(command.ExpectedDepartureTime))
        {
            yield return "Expected stay times must use minute precision.";
        }

        if (command.InventoryUnitIds is null ||
            command.InventoryUnitIds.Count is 0 or > Reservation.MaximumRequestedUnits ||
            command.InventoryUnitIds.Any(id => id == Guid.Empty) ||
            command.InventoryUnitIds.Distinct().Count() != command.InventoryUnitIds.Count)
        {
            yield return "InventoryUnitIds must contain unique, non-empty ids within the supported limit.";
        }

        if (command.ExpectedDetailsRevision <= 0)
        {
            yield return "ExpectedDetailsRevision must be greater than zero.";
        }

        if (string.IsNullOrWhiteSpace(command.ActorId) ||
            command.ActorId.Trim().Length > Reservation.ActorIdMaxLength ||
            command.ActorId.Any(char.IsControl))
        {
            yield return "ActorId is required, bounded, and control-character free.";
        }
    }

    private static bool HasMinutePrecision(TimeOnly? time) =>
        !time.HasValue || time.Value.Ticks % TimeSpan.TicksPerMinute == 0;
}
