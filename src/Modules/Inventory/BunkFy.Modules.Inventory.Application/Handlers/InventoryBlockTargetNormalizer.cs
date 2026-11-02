namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;

internal static class InventoryBlockTargetNormalizer
{
    public static bool TryNormalize(
        InventoryBlockTarget? target,
        out InventoryBlockTarget normalized)
    {
        normalized = null!;
        if (target is null)
        {
            return false;
        }

        switch (target.Kind)
        {
            case InventoryBlockTargetKind.Unspecified:
                return false;
            case InventoryBlockTargetKind.Property:
                normalized = new(InventoryBlockTargetKind.Property);
                return true;
            case InventoryBlockTargetKind.Building:
                if (!TryNormalizeRequiredLabel(
                        target.BuildingLabel,
                        out string building))
                {
                    return false;
                }

                normalized = new(
                    InventoryBlockTargetKind.Building,
                    BuildingLabel: building);
                return true;
            case InventoryBlockTargetKind.Floor:
                if (!TryNormalizeRequiredLabel(
                        target.FloorLabel,
                        out string floor) ||
                    !TryNormalizeOptionalLabel(
                        target.BuildingLabel,
                        out string? floorBuilding))
                {
                    return false;
                }

                normalized = new(
                    InventoryBlockTargetKind.Floor,
                    BuildingLabel: floorBuilding,
                    FloorLabel: floor);
                return true;
            case InventoryBlockTargetKind.Room when
                target.RoomId is { } roomId && roomId != Guid.Empty:
                normalized = new(
                    InventoryBlockTargetKind.Room,
                    RoomId: roomId);
                return true;
            case InventoryBlockTargetKind.Unit when
                target.InventoryUnitId is { } unitId &&
                unitId != Guid.Empty:
                normalized = new(
                    InventoryBlockTargetKind.Unit,
                    InventoryUnitId: unitId);
                return true;
            case InventoryBlockTargetKind.Room:
            case InventoryBlockTargetKind.Unit:
            default:
                return false;
        }
    }

    private static bool TryNormalizeOptionalLabel(
        string? value,
        out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = null;
            return true;
        }

        bool valid = TryNormalizeRequiredLabel(value, out string required);
        normalized = valid ? required : null;
        return valid;
    }

    private static bool TryNormalizeRequiredLabel(
        string? value,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        // Properties defines this shared limit with .NET string.Length. That
        // counts UTF-16 code units (not UTF-8 bytes) and is fail-closed relative
        // to PostgreSQL varchar(n), which cannot accept fewer Unicode characters.
        return normalized.Length is > 0 and
            <= PropertiesContractLimits.PhysicalLabelMaxLength &&
            !normalized.Any(char.IsControl);
    }
}
