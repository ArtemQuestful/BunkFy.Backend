namespace BunkFy.Modules.Inventory.Tests.Contracts;

using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryOperationalSurfaceContractTests
{
    [Fact]
    public void Directory_contracts_publish_truthful_continuation_metadata()
    {
        AssertProperties<RoomInventoryListResponse>("HasMore", "Page", "PageSize", "Rooms");
        AssertProperties<ManualInventoryBlockListResponse>("Blocks", "HasMore", "Page", "PageSize");
        Assert.Equal(42, InventoryContractLimits.MaximumScheduleWindowDays);
        AssertProperties<ManualInventoryBlockGroupListResponse>(
            "BlockGroups",
            "NextCursor",
            "PageSize");
        AssertProperties<ManualInventoryBlockGroupMemberListResponse>(
            "Blocks",
            "NextCursor",
            "PageSize");
        Assert.Equal(
            typeof(string),
            typeof(ListManualInventoryBlockGroupsQuery).GetProperty("Cursor")!.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(ListManualInventoryBlockGroupMembersQuery).GetProperty("Cursor")!.PropertyType);
    }

    [Fact]
    public void Mutation_receipts_publish_reconciliation_fields()
    {
        AssertProperties<RoomInventoryMutationReceiptDto>(
            "PropertyId",
            "RoomId",
            "SalesMode",
            "Version");
        AssertProperties<ManualInventoryBlockMutationReceiptDto>(
            "BlockGroupId",
            "BlockId",
            "PropertyId",
            "Status",
            "Version");
        AssertProperties<ManualInventoryBlockGroupMutationReceiptDto>(
            "ActiveBlockCount",
            "AffectedBlockCount",
            "AlreadyReleasedBlockCount",
            "BlockGroupId",
            "CreatedBlockCount",
            "CreatedNowBlockCount",
            "MembershipDigest",
            "PreviousBlockGroupId",
            "PropertyId",
            "ReleasedBlockCount",
            "ReleasedNowBlockCount",
            "ResultBlockGroupId",
            "Status",
            "TotalBlockCount",
            "Version");
        Assert.Equal(
            typeof(ManualInventoryBlockGroupStatus?),
            typeof(ManualInventoryBlockGroupMutationReceiptDto).GetProperty("Status")!.PropertyType);
        Assert.Equal(
            typeof(long?),
            typeof(ManualInventoryBlockGroupMutationReceiptDto).GetProperty("Version")!.PropertyType);

        foreach (string propertyName in new[]
                 {
                     "ReleasedBlockCount",
                     "CreatedBlockCount",
                     "TotalBlockCount",
                     "ActiveBlockCount",
                     "AlreadyReleasedBlockCount",
                     "ReleasedNowBlockCount",
                     "CreatedNowBlockCount",
                 })
        {
            Assert.Equal(
                typeof(int?),
                typeof(ManualInventoryBlockGroupMutationReceiptDto).GetProperty(propertyName)!.PropertyType);
        }
    }

    [Fact]
    public void Block_group_contracts_publish_cap_preview_and_recovery_state()
    {
        Assert.Equal(
            500,
            InventoryContractLimits.MaximumManualInventoryBlockGroupMembers);
        AssertProperties<ManualInventoryBlockGroupSelectionPreviewDto>(
            "AffectedBlockCount",
            "Arrival",
            "AtLeastAffectedBlockCount",
            "BlockGroupId",
            "BlockGroupVersion",
            "Departure",
            "ExceedsMaximumAffectedBlockCount",
            "HasActiveAllocationConflict",
            "HasManualBlockConflict",
            "HasMoreMembers",
            "IsNoOpReplacement",
            "MaximumAffectedBlockCount",
            "Members",
            "MembershipDigest",
            "MembershipDigestVersion",
            "PropertyId",
            "SelectionDigest",
            "Status",
            "Target");
        AssertProperties<ManualInventoryBlockGroupOperationDto>(
            "CompletedAtUtc",
            "Kind",
            "OperationId",
            "PropertyId",
            "Receipt",
            "RequestedBlockGroupId",
            "Status");
    }

    private static void AssertProperties<T>(params string[] expected)
    {
        string[] actual = typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}
