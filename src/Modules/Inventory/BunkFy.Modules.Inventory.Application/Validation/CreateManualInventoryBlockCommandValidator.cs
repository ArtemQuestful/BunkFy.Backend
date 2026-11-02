namespace BunkFy.Modules.Inventory.Application.Validation;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Domain.Aggregates;

internal sealed class CreateManualInventoryBlockCommandValidator : ICommandValidator<CreateManualInventoryBlockCommand>
{
    public IEnumerable<string> Validate(CreateManualInventoryBlockCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (command.InventoryUnitId == Guid.Empty)
        {
            yield return "InventoryUnitId is required.";
        }

        if (command.Arrival >= command.Departure)
        {
            yield return "Arrival must be before Departure.";
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidReason(command.Reason))
        {
            yield return $"Reason is required and must be {ManualInventoryBlock.ReasonMaxLength} characters or fewer.";
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidActor(command.ActorId))
        {
            yield return $"ActorId is required and must be a control-free value of {ManualInventoryBlockGroup.ActorIdMaxLength} characters or fewer.";
        }
    }
}
