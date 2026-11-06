namespace BunkFy.Modules.Inventory.Tests.Application;

using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Application.Validation;
using BunkFy.Modules.Inventory.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ListManualInventoryBlocksQueryValidatorTests
{
    private readonly ListManualInventoryBlocksQueryValidator validator = new();

    [Fact]
    public void Accepts_a_complete_bounded_overlap_window()
    {
        ListManualInventoryBlocksQuery query = Query(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 1).AddDays(InventoryContractLimits.MaximumScheduleWindowDays));

        Assert.Empty(this.validator.Validate(query));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rejects_a_partial_overlap_window(bool includeFrom, bool includeTo)
    {
        DateOnly date = new(2026, 8, 1);
        ListManualInventoryBlocksQuery query = Query(
            includeFrom ? date : null,
            includeTo ? date.AddDays(1) : null);

        Assert.Contains(
            "OverlapsFrom and OverlapsTo must be supplied together.",
            this.validator.Validate(query));
    }

    [Fact]
    public void Rejects_an_overlap_window_above_the_contract_limit()
    {
        DateOnly from = new(2026, 8, 1);
        ListManualInventoryBlocksQuery query = Query(
            from,
            from.AddDays(InventoryContractLimits.MaximumScheduleWindowDays + 1));

        Assert.Contains(
            $"The inventory block overlap window cannot exceed {InventoryContractLimits.MaximumScheduleWindowDays} days.",
            this.validator.Validate(query));
    }

    private static ListManualInventoryBlocksQuery Query(
        DateOnly? overlapsFrom,
        DateOnly? overlapsTo) => new(
        Guid.NewGuid(),
        InventoryUnitId: null,
        IncludeReleased: false,
        Page: 1,
        PageSize: 100,
        overlapsFrom,
        overlapsTo);
}
