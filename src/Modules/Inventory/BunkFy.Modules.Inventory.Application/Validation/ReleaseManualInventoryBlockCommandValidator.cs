namespace BunkFy.Modules.Inventory.Application.Validation;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Domain.Aggregates;

internal sealed class ReleaseManualInventoryBlockCommandValidator : ICommandValidator<ReleaseManualInventoryBlockCommand>
{
    public IEnumerable<string> Validate(ReleaseManualInventoryBlockCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (command.BlockId == Guid.Empty)
        {
            yield return "BlockId is required.";
        }

        if (command.ExpectedVersion <= 0)
        {
            yield return "ExpectedVersion must be greater than zero.";
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidActor(command.ActorId))
        {
            yield return $"ActorId is required and must be a control-free value of {ManualInventoryBlockGroup.ActorIdMaxLength} characters or fewer.";
        }
    }
}
