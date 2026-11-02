namespace BunkFy.Modules.Inventory.Tests;

using System.Globalization;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ManualInventoryBlockGroupDigestTests
{
    [Theory]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    public void Membership_digest_is_culture_invariant_and_uses_utf8_lengths(string cultureName)
    {
        InventoryBlockTarget target = new(
            InventoryBlockTargetKind.Floor,
            BuildingLabel: "A😀",
            FloorLabel: "ชั้น");
        Guid[] ids =
        [
            Guid.Parse("00000001-0000-0000-0000-000000000000"),
            Guid.Parse("00000000-0100-0000-0000-000000000000")
        ];
        string expectedPayload = ManualInventoryBlockGroupDigest.BuildMembershipPayload(
            target,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            ids);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            Assert.Equal(expectedPayload, ManualInventoryBlockGroupDigest.BuildMembershipPayload(
                target,
                new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 3),
                ids.Reverse().ToArray()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Contains("|building=5:A😀|", expectedPayload, StringComparison.Ordinal);
        Assert.Contains("|floor=12:ชั้น|", expectedPayload, StringComparison.Ordinal);
        Assert.EndsWith(
            "|00000000010000000000000000000000,00000001000000000000000000000000",
            expectedPayload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_digest_binds_range_and_persisted_selection_coordinates()
    {
        InventoryBlockTarget target = new(InventoryBlockTargetKind.Property);
        InventoryBlockSelectionCoordinate coordinate = new(
            Guid.Parse("10000000-0000-0000-0000-000000000000"),
            3,
            2,
            7,
            1,
            Guid.Parse("20000000-0000-0000-0000-000000000000"),
            "Room 1",
            4,
            4,
            1,
            2,
            5,
            8,
            6,
            6,
            true,
            9,
            [new(Guid.Parse("30000000-0000-0000-0000-000000000000"), 1, 2, 1)]);

        string baseline = ManualInventoryBlockGroupDigest.ComputeSelection(
            target,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            [coordinate]);

        Assert.NotEqual(baseline, ManualInventoryBlockGroupDigest.ComputeSelection(
            target,
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 3),
            [coordinate]));
        Assert.NotEqual(baseline, ManualInventoryBlockGroupDigest.ComputeSelection(
            target,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            [coordinate with { UnitAvailabilityMutationVersion = 10 }]));
        Assert.NotEqual(baseline, ManualInventoryBlockGroupDigest.ComputeSelection(
            target,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            [coordinate with { PropertyAvailabilitySelectionVersion = 8 }]));
    }
}
