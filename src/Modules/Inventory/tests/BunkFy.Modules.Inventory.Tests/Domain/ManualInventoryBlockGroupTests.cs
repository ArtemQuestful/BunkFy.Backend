namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ManualInventoryBlockGroupTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Native_create_requires_actor_preview_proof_and_exact_cap()
    {
        Assert.Equal(
            InventoryContractLimits.MaximumManualInventoryBlockGroupMembers,
            ManualInventoryBlockGroup.MaximumMemberCount);

        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            Create(1, actorId: null).Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupSelectionInvalid,
            Create(1, actorId: "operator-a", selectionDigest: null).Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupSelectionInvalid,
            Create(ManualInventoryBlockGroup.MaximumMemberCount + 1, "operator-a").Error);

        Result<ManualInventoryBlockGroup> result = Create(
            ManualInventoryBlockGroup.MaximumMemberCount,
            "operator-a");
        Assert.True(result.IsSuccess);
        Assert.Equal(ManualInventoryBlockGroupState.Active, result.Value.State);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal("operator-a", result.Value.CreatedByActorId);
    }

    [Fact]
    public void Member_release_advances_parent_exactly_once_with_transition_actor_provenance()
    {
        ManualInventoryBlockGroup group = Create(2, "operator-a").Value;

        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            group.RecordMemberRelease(Now.AddMinutes(1), actorId: null).Error);
        Assert.Equal(2, group.ActiveBlockCount);
        Assert.Equal(1, group.Version);

        Assert.True(group.RecordMemberRelease(Now.AddMinutes(1), "operator-b").IsSuccess);
        Assert.Equal(ManualInventoryBlockGroupState.PartiallyReleased, group.State);
        Assert.Equal(1, group.ActiveBlockCount);
        Assert.Equal(2, group.Version);
        Assert.Equal("operator-b", group.LastModifiedByActorId);

        Assert.True(group.RecordMemberRelease(Now.AddMinutes(2), "operator-c").IsSuccess);
        Assert.Equal(ManualInventoryBlockGroupState.Released, group.State);
        Assert.Equal(0, group.ActiveBlockCount);
        Assert.Equal(3, group.Version);
        Assert.Equal("operator-c", group.LastModifiedByActorId);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupAlreadyTerminal,
            group.RecordMemberRelease(Now.AddMinutes(3), "operator-b").Error);
    }

    [Fact]
    public void Every_terminal_transition_requires_control_free_actor()
    {
        ManualInventoryBlockGroup release = Create(1, "operator-a").Value;
        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            release.Release(1, 1, Now.AddMinutes(1), actorId: null).Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            release.Release(1, 1, Now.AddMinutes(1), "bad\nactor").Error);
        Assert.Equal(ManualInventoryBlockGroupState.Active, release.State);

        ManualInventoryBlockGroup replace = Create(1, "operator-a").Value;
        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            replace.ReplaceWith(1, Guid.NewGuid(), 1, Now.AddMinutes(1), "\t").Error);
        Assert.Equal(ManualInventoryBlockGroupState.Active, replace.State);
    }

    [Fact]
    public void Create_rejects_control_characters_in_reason_and_actor()
    {
        Assert.Equal(
            InventoryDomainErrors.BlockReasonInvalid,
            Create(1, "operator-a", reason: "Maintenance\nwindow").Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupActorInvalid,
            Create(1, "operator\ta").Error);
    }

    [Fact]
    public void Native_create_enforces_exact_target_shape_and_label_policy()
    {
        Assert.Equal(
            PropertiesContractLimits.PhysicalLabelMaxLength,
            ManualInventoryBlockGroup.TargetLabelMaxLength);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Room,
                buildingLabel: "North",
                roomId: Guid.NewGuid()).Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Unit,
                floorLabel: "2",
                roomId: Guid.NewGuid(),
                inventoryUnitId: Guid.NewGuid()).Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Building,
                buildingLabel: "North\nWing").Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Building,
                buildingLabel: " \t ").Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Floor,
                floorLabel: new string('x',
                    ManualInventoryBlockGroup.TargetLabelMaxLength + 1)).Error);

        Result<ManualInventoryBlockGroup> asciiBoundary = CreateTarget(
            ManualInventoryBlockGroupTargetKind.Floor,
            floorLabel: new string(
                'x',
                ManualInventoryBlockGroup.TargetLabelMaxLength));
        Assert.True(asciiBoundary.IsSuccess);

        Result<ManualInventoryBlockGroup> boundary = CreateTarget(
            ManualInventoryBlockGroupTargetKind.Building,
            buildingLabel: string.Concat(Enumerable.Repeat(
                "\U0001F3E8",
                ManualInventoryBlockGroup.TargetLabelMaxLength / 2)));
        Assert.True(boundary.IsSuccess);
        Assert.Equal(
            ManualInventoryBlockGroup.TargetLabelMaxLength,
            boundary.Value.BuildingLabel!.Length);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTargetInvalid,
            CreateTarget(
                ManualInventoryBlockGroupTargetKind.Building,
                buildingLabel: string.Concat(Enumerable.Repeat(
                    "\U0001F3E8",
                    (ManualInventoryBlockGroup.TargetLabelMaxLength / 2) + 1)))
                .Error);
    }

    [Fact]
    public void Replace_terminalizes_immutable_predecessor_and_rejects_stale_or_backward_time()
    {
        ManualInventoryBlockGroup group = Create(2, "operator-a").Value;
        Guid successorId = Guid.NewGuid();

        Assert.Equal(
            InventoryDomainErrors.VersionConflict,
            group.ReplaceWith(99, successorId, 2, Now.AddMinutes(1), "operator-b").Error);
        Assert.Equal(
            InventoryDomainErrors.BlockGroupTimestampInvalid,
            group.ReplaceWith(1, successorId, 2, Now.AddMinutes(-1), "operator-b").Error);

        Assert.True(group.ReplaceWith(1, successorId, 2, Now.AddMinutes(1), "operator-b").IsSuccess);
        Assert.Equal(ManualInventoryBlockGroupState.Replaced, group.State);
        Assert.Equal(0, group.ActiveBlockCount);
        Assert.Equal(2, group.Version);
        Assert.Null(group.ReplacesGroupId);
        Assert.Equal(new DateOnly(2026, 9, 1), group.Arrival);
        Assert.Equal("Maintenance", group.Reason);
    }

    private static Result<ManualInventoryBlockGroup> Create(
        int memberCount,
        string? actorId,
        string? selectionDigest = Digest,
        string reason = " Maintenance ") => ManualInventoryBlockGroup.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            ManualInventoryBlockGroupTargetKind.Property,
            buildingLabel: null,
            floorLabel: null,
            roomId: null,
            inventoryUnitId: null,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            reason,
            selectionDigest,
            Digest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            memberCount,
            replacesGroupId: null,
            Now,
            actorId);

    private static Result<ManualInventoryBlockGroup> CreateTarget(
        ManualInventoryBlockGroupTargetKind targetKind,
        string? buildingLabel = null,
        string? floorLabel = null,
        Guid? roomId = null,
        Guid? inventoryUnitId = null) => ManualInventoryBlockGroup.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            targetKind,
            buildingLabel,
            floorLabel,
            roomId,
            inventoryUnitId,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            "Maintenance",
            Digest,
            Digest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount: 1,
            replacesGroupId: null,
            Now,
            "operator-a");
}
