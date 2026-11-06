namespace BunkFy.Modules.Inventory.Application.Validation;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class CreateManualInventoryBlockGroupCommandValidator
    : ICommandValidator<CreateManualInventoryBlockGroupCommand>
{
    public IEnumerable<string> Validate(CreateManualInventoryBlockGroupCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
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

        if (!IsValidReason(command.Reason))
        {
            yield return $"Reason is required and must be {ManualInventoryBlock.ReasonMaxLength} characters or fewer.";
        }

        if (!IsSha256(command.ExpectedSelectionDigest))
        {
            yield return "ExpectedSelectionDigest must be a lowercase SHA-256 value.";
        }

        if (command.ExpectedAffectedBlockCount is < 1 or > InventoryContractLimits.MaximumManualInventoryBlockGroupMembers)
        {
            yield return $"ExpectedAffectedBlockCount must be between 1 and {InventoryContractLimits.MaximumManualInventoryBlockGroupMembers}.";
        }

        if (!IsValidActor(command.ActorId))
        {
            yield return $"ActorId is required and must be a control-free value of {ManualInventoryBlockGroup.ActorIdMaxLength} characters or fewer.";
        }
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal static bool IsValidReason(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ManualInventoryBlock.ReasonMaxLength &&
            !normalized.Any(char.IsControl);
    }

    internal static bool IsValidActor(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ManualInventoryBlockGroup.ActorIdMaxLength &&
            !normalized.Any(char.IsControl);
    }
}
