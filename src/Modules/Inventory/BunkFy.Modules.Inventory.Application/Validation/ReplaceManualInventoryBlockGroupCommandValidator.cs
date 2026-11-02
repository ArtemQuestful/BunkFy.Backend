namespace BunkFy.Modules.Inventory.Application.Validation;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class ReplaceManualInventoryBlockGroupCommandValidator
    : ICommandValidator<ReplaceManualInventoryBlockGroupCommand>
{
    public IEnumerable<string> Validate(ReplaceManualInventoryBlockGroupCommand command)
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

        if (!InventoryBlockTargetNormalizer.TryNormalize(
                command.Target,
                out _))
        {
            yield return "Target must identify a property, building, floor, room, or inventory unit.";
        }

        if (command.Arrival >= command.Departure)
        {
            yield return "Arrival must be before Departure.";
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidReason(command.Reason))
        {
            yield return $"Reason is required and must be {ManualInventoryBlock.ReasonMaxLength} characters or fewer.";
        }
        if (!CreateManualInventoryBlockGroupCommandValidator.IsSha256(command.ExpectedSelectionDigest))
        {
            yield return "ExpectedSelectionDigest must be a lowercase SHA-256 value.";
        }
        if (command.ExpectedAffectedBlockCount is < 1 or > InventoryContractLimits.MaximumManualInventoryBlockGroupMembers)
        {
            yield return $"ExpectedAffectedBlockCount must be between 1 and {InventoryContractLimits.MaximumManualInventoryBlockGroupMembers}.";
        }
        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidActor(command.ActorId))
        {
            yield return $"ActorId is required and must be a control-free value of {ManualInventoryBlockGroup.ActorIdMaxLength} characters or fewer.";
        }
    }
}
