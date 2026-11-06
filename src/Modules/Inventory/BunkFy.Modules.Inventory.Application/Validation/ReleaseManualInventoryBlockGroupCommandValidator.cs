namespace BunkFy.Modules.Inventory.Application.Validation;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class ReleaseManualInventoryBlockGroupCommandValidator
    : ICommandValidator<ReleaseManualInventoryBlockGroupCommand>
{
    public IEnumerable<string> Validate(ReleaseManualInventoryBlockGroupCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (command.BlockGroupId == Guid.Empty)
        {
            yield return "BlockGroupId is required.";
        }

        if (command.ExpectedVersion <= 0)
        {
            yield return "ExpectedVersion must be positive.";
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidActor(command.ActorId))
        {
            yield return $"ActorId is required and must be a control-free value of {ManualInventoryBlockGroup.ActorIdMaxLength} characters or fewer.";
        }
    }
}
