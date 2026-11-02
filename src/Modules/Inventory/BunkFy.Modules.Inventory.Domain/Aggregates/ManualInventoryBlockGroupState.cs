namespace BunkFy.Modules.Inventory.Domain.Aggregates;

public enum ManualInventoryBlockGroupState
{
    Active = 1,
    PartiallyReleased = 2,
    Released = 3,
    Replaced = 4
}
