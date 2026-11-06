namespace BunkFy.Modules.Inventory.Tests.Application;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Application.Validation;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ManualInventoryBlockGroupProtocolTests
{
    private static readonly Guid PropertyId =
        Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("71000000-0000-0000-0000-000000000002");
    private static readonly Guid UnitId =
        Guid.Parse("71000000-0000-0000-0000-000000000003");
    private static readonly DateOnly Arrival = new(2026, 9, 1);
    private static readonly DateOnly Departure = new(2026, 9, 3);
    private static readonly InventoryBlockTarget Target = new(
        InventoryBlockTargetKind.Property);

    [Fact]
    public async Task Unconfirmed_mutations_reach_handlers_and_return_the_stable_confirmation_error()
    {
        CreateManualInventoryBlockGroupCommand createCommand = new(
            Guid.NewGuid(),
            PropertyId,
            Target,
            Arrival,
            Departure,
            "Maintenance",
            new string('a', 64),
            ExpectedAffectedBlockCount: 1,
            Confirmed: false,
            ActorId: "user:operator");
        ReplaceManualInventoryBlockGroupCommand replaceCommand = new(
            Guid.NewGuid(),
            PropertyId,
            Guid.NewGuid(),
            ExpectedVersion: 1,
            Target,
            Arrival,
            Departure,
            "Maintenance",
            new string('a', 64),
            ExpectedAffectedBlockCount: 1,
            Confirmed: false,
            ActorId: "user:operator");
        ReleaseManualInventoryBlockGroupCommand releaseCommand = new(
            Guid.NewGuid(),
            PropertyId,
            Guid.NewGuid(),
            ExpectedVersion: 1,
            Confirmed: false,
            ActorId: "user:operator");

        Assert.Empty(new CreateManualInventoryBlockGroupCommandValidator()
            .Validate(createCommand));
        Assert.Empty(new ReplaceManualInventoryBlockGroupCommandValidator()
            .Validate(replaceCommand));
        Assert.Empty(new ReleaseManualInventoryBlockGroupCommandValidator()
            .Validate(releaseCommand));

        Result<ManualInventoryBlockGroupMutationReceiptDto> create =
            await new CreateManualInventoryBlockGroupCommandHandler(
                    null!,
                    null!,
                    null!,
                    null!)
                .HandleAsync(createCommand, CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> replace =
            await new ReplaceManualInventoryBlockGroupCommandHandler(
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!)
                .HandleAsync(replaceCommand, CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> release =
            await new ReleaseManualInventoryBlockGroupCommandHandler(
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!,
                    null!)
                .HandleAsync(releaseCommand, CancellationToken.None);

        Assert.Equal(
            InventoryApplicationErrors.BlockGroupConfirmationRequired,
            create.Error);
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupConfirmationRequired,
            replace.Error);
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupConfirmationRequired,
            release.Error);
    }

    [Fact]
    public void Target_labels_use_the_shared_utf16_length_and_control_free_policy()
    {
        string exact = new(
            'x',
            PropertiesContractLimits.PhysicalLabelMaxLength);
        string exactNonBmp = string.Concat(Enumerable.Repeat(
            "🏨",
            PropertiesContractLimits.PhysicalLabelMaxLength / 2));
        string oversizedNonBmp = exactNonBmp + "🏨";

        Assert.True(InventoryBlockTargetNormalizer.TryNormalize(
            new(InventoryBlockTargetKind.Building, BuildingLabel: $" {exact} "),
            out InventoryBlockTarget building));
        Assert.Equal(exact, building.BuildingLabel);
        Assert.True(InventoryBlockTargetNormalizer.TryNormalize(
            new(InventoryBlockTargetKind.Floor, FloorLabel: exactNonBmp),
            out InventoryBlockTarget floor));
        Assert.Equal(exactNonBmp, floor.FloorLabel);
        Assert.False(InventoryBlockTargetNormalizer.TryNormalize(
            new(InventoryBlockTargetKind.Floor, FloorLabel: oversizedNonBmp),
            out _));
    }

    [Theory]
    [InlineData(InventoryBlockTargetKind.Building, 0)]
    [InlineData(InventoryBlockTargetKind.Building, 1)]
    [InlineData(InventoryBlockTargetKind.Floor, 0)]
    [InlineData(InventoryBlockTargetKind.Floor, 1)]
    public async Task Invalid_target_labels_fail_before_preview_create_and_replace_dependencies(
        InventoryBlockTargetKind kind,
        int invalidShape)
    {
        string invalid = invalidShape == 0
            ? new string(
                'x',
                PropertiesContractLimits.PhysicalLabelMaxLength + 1)
            : "bad\nlabel";
        InventoryBlockTarget invalidTarget = kind ==
            InventoryBlockTargetKind.Building
                ? new(kind, BuildingLabel: invalid)
                : new(kind, FloorLabel: invalid);
        PreviewManualInventoryBlockGroupQueryHandler preview = new(
            null!,
            null!,
            null!);
        CreateManualInventoryBlockGroupCommandHandler create = new(
            null!,
            null!,
            null!,
            null!);
        ReplaceManualInventoryBlockGroupCommandHandler replace = new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> previewResult =
            await preview.HandleAsync(
                new(
                    PropertyId,
                    invalidTarget,
                    Arrival,
                    Departure,
                    "Maintenance"),
                CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> createResult =
            await create.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyId,
                    invalidTarget,
                    Arrival,
                    Departure,
                    "Maintenance",
                    new string('a', 64),
                    ExpectedAffectedBlockCount: 1,
                    Confirmed: true,
                    ActorId: "user:operator"),
                CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> replaceResult =
            await replace.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyId,
                    Guid.NewGuid(),
                    ExpectedVersion: 1,
                    invalidTarget,
                    Arrival,
                    Departure,
                    "Maintenance",
                    new string('a', 64),
                    ExpectedAffectedBlockCount: 1,
                    Confirmed: true,
                    ActorId: "user:operator"),
                CancellationToken.None);

        Assert.Equal(
            InventoryApplicationErrors.BlockTargetInvalid,
            previewResult.Error);
        Assert.Equal(
            InventoryApplicationErrors.BlockTargetInvalid,
            createResult.Error);
        Assert.Equal(
            InventoryApplicationErrors.BlockTargetInvalid,
            replaceResult.Error);
    }

    [Fact]
    public async Task Empty_preview_is_exact_and_has_no_digest_or_effects()
    {
        SequenceInventoryReadRepository inventory = new(BoundaryResolution(0));
        RecordingAvailabilityRepository availability = new();
        PreviewManualInventoryBlockGroupQueryHandler handler = new(
            null!,
            null!,
            Creator(inventory, availability));

        Result<ManualInventoryBlockGroupSelectionPreviewDto> result =
            await handler.HandleAsync(
                new(PropertyId, Target, Arrival, Departure, "Maintenance"),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.Empty,
            result.Value.Status);
        Assert.Equal(0, result.Value.AffectedBlockCount);
        Assert.Equal(0, result.Value.AtLeastAffectedBlockCount);
        Assert.Null(result.Value.SelectionDigest);
        Assert.Null(result.Value.MembershipDigest);
        Assert.Empty(result.Value.Members);
        Assert.False(result.Value.HasMoreMembers);
        Assert.Equal(0, availability.TouchCount);
        Assert.Equal(0, availability.ConflictReadCount);
    }

    [Fact]
    public async Task Five_hundred_member_create_materializes_one_atomic_group_and_receipt()
    {
        InventoryBlockTargetResolution resolution = BoundaryResolution(
            InventoryContractLimits.MaximumManualInventoryBlockGroupMembers);
        SequenceInventoryReadRepository inventory = new(resolution, resolution);
        RecordingAvailabilityRepository availability = new();
        RecordingGroupRepository groups = new();
        RecordingBlockRepository blocks = new();
        RecordingOperationRepository operations = new();
        TestScopeContext scope = new();
        ManualInventoryBlockCreator creator = new(
            inventory,
            blocks,
            groups,
            availability,
            scope,
            new TestClock(),
            new SequentialIdGenerator());
        CreateManualInventoryBlockGroupCommandHandler handler = new(
            new(new RecordingLock(), scope),
            new(operations),
            creator,
            new RecordingSelectionFence());
        string digest = ManualInventoryBlockGroupDigest.ComputeSelection(
            Target,
            Arrival,
            Departure,
            resolution.SelectionCoordinates);

        Result<ManualInventoryBlockGroupMutationReceiptDto> result =
            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyId,
                    Target,
                    Arrival,
                    Departure,
                    "Maintenance",
                    digest,
                    InventoryContractLimits.MaximumManualInventoryBlockGroupMembers,
                    Confirmed: true,
                    ActorId: "user:operator"),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(500, result.Value.TotalBlockCount);
        Assert.Equal(500, result.Value.ActiveBlockCount);
        Assert.Equal(500, result.Value.CreatedNowBlockCount);
        Assert.Single(groups.Added);
        Assert.Equal(500, Assert.Single(blocks.AddedBatches).Count);
        Assert.Single(operations.Added);
        Assert.Equal(
            InventoryManagementMutationKind.ManualBlockGroupCreateV2,
            operations.Added[0].Kind);
        Assert.Equal(1, availability.TouchCount);
        Assert.Equal(1, availability.ConflictReadCount);
        Assert.Equal([501, 501], inventory.MaximumUnitCounts);
    }

    [Fact]
    public async Task Five_hundred_and_one_member_preview_and_create_are_bounded_and_write_nothing()
    {
        InventoryBlockTargetResolution resolution = BoundaryResolution(
            InventoryContractLimits.MaximumManualInventoryBlockGroupMembers + 1);
        SequenceInventoryReadRepository previewInventory = new(resolution);
        RecordingAvailabilityRepository previewAvailability = new();
        PreviewManualInventoryBlockGroupQueryHandler previewHandler = new(
            null!,
            null!,
            Creator(previewInventory, previewAvailability));

        Result<ManualInventoryBlockGroupSelectionPreviewDto> preview =
            await previewHandler.HandleAsync(
                new(PropertyId, Target, Arrival, Departure, "Maintenance"),
                CancellationToken.None);

        Assert.True(preview.IsSuccess, preview.Error.Code);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.TooLarge,
            preview.Value.Status);
        Assert.Null(preview.Value.AffectedBlockCount);
        Assert.Equal(501, preview.Value.AtLeastAffectedBlockCount);
        Assert.True(preview.Value.ExceedsMaximumAffectedBlockCount);
        Assert.Null(preview.Value.SelectionDigest);
        Assert.Null(preview.Value.MembershipDigest);
        Assert.Equal(25, preview.Value.Members.Count);
        Assert.True(preview.Value.HasMoreMembers);
        Assert.Equal([501], previewInventory.MaximumUnitCounts);
        Assert.Equal(0, previewAvailability.TouchCount);
        Assert.Equal(0, previewAvailability.ConflictReadCount);

        SequenceInventoryReadRepository createInventory = new(resolution);
        RecordingAvailabilityRepository createAvailability = new();
        RecordingGroupRepository groups = new();
        RecordingBlockRepository blocks = new();
        RecordingOperationRepository operations = new();
        TestScopeContext scope = new();
        CreateManualInventoryBlockGroupCommandHandler createHandler = new(
            new(new RecordingLock(), scope),
            new(operations),
            new(
                createInventory,
                blocks,
                groups,
                createAvailability,
                scope,
                new TestClock(),
                new SequentialIdGenerator()),
            new RecordingSelectionFence());

        Result<ManualInventoryBlockGroupMutationReceiptDto> create =
            await createHandler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyId,
                    Target,
                    Arrival,
                    Departure,
                    "Maintenance",
                    new string('a', 64),
                    ExpectedAffectedBlockCount: 500,
                    Confirmed: true,
                    ActorId: "user:operator"),
                CancellationToken.None);

        Assert.Equal(
            InventoryApplicationErrors.BlockGroupTargetTooLarge,
            create.Error);
        Assert.Empty(groups.Added);
        Assert.Empty(blocks.AddedBatches);
        Assert.Empty(operations.Added);
        Assert.Equal(0, createAvailability.TouchCount);
        Assert.Equal(0, createAvailability.ConflictReadCount);
        Assert.Equal([501], createInventory.MaximumUnitCounts);
    }

    [Fact]
    public async Task Unchanged_persisted_selection_succeeds_after_local_optimistic_fence()
    {
        InventoryBlockTargetResolution stable = Resolution(7);
        SequenceInventoryReadRepository inventory = new(stable, stable);
        RecordingAvailabilityRepository availability = new();
        ManualInventoryBlockCreator creator = Creator(inventory, availability);
        string previewDigest = ManualInventoryBlockGroupDigest.ComputeSelection(
            Target,
            Arrival,
            Departure,
            stable.SelectionCoordinates);

        Result<ManualInventoryBlockSelection> result = await creator.ConfirmAsync(
            PropertyId,
            Target,
            Arrival,
            Departure,
            previewDigest,
            expectedAffectedBlockCount: 1,
            excludedBlockIds: [],
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, availability.TouchCount);
        Assert.Equal(1, availability.TrackedFenceVersion);
        Assert.Equal(2, inventory.ResolveCount);
    }

    [Fact]
    public async Task Persisted_selection_change_during_fence_fails_confirmation()
    {
        InventoryBlockTargetResolution preview = Resolution(7);
        SequenceInventoryReadRepository inventory = new(preview, Resolution(8));
        RecordingAvailabilityRepository availability = new();
        ManualInventoryBlockCreator creator = Creator(inventory, availability);
        string previewDigest = ManualInventoryBlockGroupDigest.ComputeSelection(
            Target,
            Arrival,
            Departure,
            preview.SelectionCoordinates);

        Result<ManualInventoryBlockSelection> result = await creator.ConfirmAsync(
            PropertyId,
            Target,
            Arrival,
            Departure,
            previewDigest,
            expectedAffectedBlockCount: 1,
            excludedBlockIds: [],
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockGroupSelectionMismatch, result.Error);
        Assert.Equal(1, availability.TouchCount);
        Assert.Equal(2, inventory.ResolveCount);
    }

    [Fact]
    public async Task Semantic_replace_no_op_and_exact_replay_never_touch_availability_fences()
    {
        InventoryBlockTargetResolution resolution = Resolution(7);
        SequenceInventoryReadRepository inventory = new(resolution);
        RecordingAvailabilityRepository availability = new();
        string selectionDigest = ManualInventoryBlockGroupDigest.ComputeSelection(
            Target,
            Arrival,
            Departure,
            resolution.SelectionCoordinates);
        string membershipDigest = ManualInventoryBlockGroupDigest.ComputeMembership(
            Target,
            Arrival,
            Departure,
            [UnitId]);
        Guid groupId = Guid.NewGuid();
        ManualInventoryBlockGroup group = ManualInventoryBlockGroup.Create(
            groupId,
            "tenant-a",
            PropertyId,
            ManualInventoryBlockGroupTargetKind.Property,
            buildingLabel: null,
            floorLabel: null,
            roomId: null,
            inventoryUnitId: null,
            Arrival,
            Departure,
            "Maintenance",
            selectionDigest,
            membershipDigest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount: 1,
            replacesGroupId: null,
            new TestClock().UtcNow,
            "user:operator").Value;
        ManualInventoryBlock block = ManualInventoryBlock.Create(
            Guid.NewGuid(),
            groupId,
            "tenant-a",
            PropertyId,
            UnitId,
            Arrival,
            Departure,
            "Maintenance",
            Guid.NewGuid(),
            new TestClock().UtcNow,
            "user:operator").Value;
        RecordingGroupRepository groups = new(group);
        RecordingBlockRepository blocks = new([block]);
        RecordingOperationRepository operations = new();
        RecordingSelectionFence selectionFence = new();
        TestScopeContext scope = new();
        ReplaceManualInventoryBlockGroupCommandHandler handler = new(
            new(new RecordingLock(), scope),
            new(operations),
            new(
                inventory,
                blocks,
                groups,
                availability,
                scope,
                new TestClock(),
                new SequentialIdGenerator()),
            selectionFence,
            groups,
            blocks,
            availability,
            null!,
            new TestClock(),
            new SequentialIdGenerator());
        ReplaceManualInventoryBlockGroupCommand command = new(
            Guid.NewGuid(),
            PropertyId,
            groupId,
            ExpectedVersion: 1,
            Target,
            Arrival,
            Departure,
            "Maintenance",
            selectionDigest,
            ExpectedAffectedBlockCount: 1,
            Confirmed: true,
            ActorId: "user:operator");

        Result<ManualInventoryBlockGroupMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(0, first.Value.AffectedBlockCount);
        Assert.Equal(groupId, first.Value.BlockGroupId);
        Assert.Equal(1, group.Version);
        Assert.Equal(ManualInventoryBlockGroupState.Active, group.State);
        Assert.Equal(1, inventory.ResolveCount);
        Assert.Equal(1, selectionFence.AcquireCount);
        Assert.Equal(0, selectionFence.AdvanceCount);
        Assert.Equal(0, availability.TouchCount);
        Assert.Equal(0, availability.ConflictReadCount);
        Assert.Empty(groups.Added);
        Assert.Empty(blocks.AddedBatches);
        Assert.Single(operations.Added);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task Preview_rejects_non_forward_range_before_repository_reads(
        int arrivalOffset,
        int departureOffset)
    {
        PreviewManualInventoryBlockGroupQueryHandler handler = new(null!, null!, null!);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> result = await handler.HandleAsync(
            new(
                PropertyId,
                Target,
                Arrival.AddDays(arrivalOffset),
                Arrival.AddDays(departureOffset),
                "Maintenance"),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.StayRangeInvalid, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nreason")]
    public async Task Preview_rejects_invalid_reason_before_repository_reads(string? reason)
    {
        PreviewManualInventoryBlockGroupQueryHandler handler = new(null!, null!, null!);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> result = await handler.HandleAsync(
            new(PropertyId, Target, Arrival, Departure, reason!, BlockGroupId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockReasonInvalid, result.Error);
    }

    [Fact]
    public async Task Preview_rejects_oversized_reason_before_repository_reads()
    {
        PreviewManualInventoryBlockGroupQueryHandler handler = new(null!, null!, null!);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> result = await handler.HandleAsync(
            new(PropertyId, Target, Arrival, Departure, new string('x', ManualInventoryBlock.ReasonMaxLength + 1)),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockReasonInvalid, result.Error);
    }

    [Theory]
    [InlineData(ManualInventoryBlockGroupStatus.Released)]
    [InlineData(ManualInventoryBlockGroupStatus.Replaced)]
    public async Task Replacement_preview_rejects_terminal_predecessor_before_member_or_selection_reads(
        ManualInventoryBlockGroupStatus status)
    {
        Guid groupId = Guid.NewGuid();
        ManualInventoryBlockGroupDto dto = new(
            groupId,
            PropertyId,
            Target,
            Arrival,
            Departure,
            "Maintenance",
            new string('a', 64),
            new string('b', 64),
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            InitialBlockCount: 1,
            ActiveBlockCount: 0,
            Status: status,
            Version: 2,
            ReplacesGroupId: null,
            ReplacedByGroupId: null,
            CreatedAtUtc: new TestClock().UtcNow,
            UpdatedAtUtc: new TestClock().UtcNow,
            ReleasedAtUtc: new TestClock().UtcNow,
            CreatedByActorId: "user:operator",
            LastModifiedByActorId: "user:operator");
        PreviewManualInventoryBlockGroupQueryHandler handler = new(
            new StaticGroupDtoRepository(dto),
            null!,
            null!);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> result = await handler.HandleAsync(
            new(
                PropertyId,
                Target,
                Arrival,
                Departure,
                "Maintenance",
                groupId,
                ExpectedVersion: 2),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockAlreadyReleased, result.Error);
    }

    [Fact]
    public async Task Group_list_maps_argument_cursor_failure_to_stable_error()
    {
        ListManualInventoryBlockGroupsQueryHandler handler = new(
            new SequenceInventoryReadRepository(),
            new ThrowingGroupRepository());

        Result<ManualInventoryBlockGroupListResponse> result = await handler.HandleAsync(
            new(PropertyId, Status: null, Cursor: "malformed", PageSize: 25),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockGroupCursorInvalid, result.Error);
    }

    [Fact]
    public async Task Group_list_maps_provider_format_cursor_failure_to_stable_error()
    {
        ListManualInventoryBlockGroupsQueryHandler handler = new(
            new SequenceInventoryReadRepository(),
            new ThrowingGroupRepository(throwFormatException: true));

        Result<ManualInventoryBlockGroupListResponse> result = await handler.HandleAsync(
            new(PropertyId, Status: null, Cursor: "malformed", PageSize: 25),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockGroupCursorInvalid, result.Error);
    }

    [Fact]
    public async Task Member_list_maps_cross_coordinate_cursor_failure_to_stable_error()
    {
        ManualInventoryBlockGroup group = CreateGroup();
        ListManualInventoryBlockGroupMembersQueryHandler handler = new(
            new ThrowingGroupRepository(group),
            new ThrowingBlockRepository());

        Result<ManualInventoryBlockGroupMemberListResponse> result = await handler.HandleAsync(
            new(PropertyId, group.Id, Status: null, Cursor: "other-property", PageSize: 25),
            CancellationToken.None);

        Assert.Equal(InventoryApplicationErrors.BlockGroupCursorInvalid, result.Error);
    }

    private static ManualInventoryBlockCreator Creator(
        IInventoryReadRepository inventory,
        IInventoryAvailabilityRepository availability) => new(
            inventory,
            null!,
            null!,
            availability,
            null!,
            null!,
            null!);

    private static InventoryBlockTargetResolution Resolution(long availabilityVersion)
    {
        InventoryUnitSnapshot unit = new(
            new InventoryUnitDto(
                UnitId,
                PropertyId,
                RoomId,
                BedId: null,
                InventoryUnitKind.Room,
                "101",
                IsSellable: true,
                IsTopologyActive: true),
            IsSellable: true);
        InventoryBlockSelectionCoordinate coordinate = new(
            UnitId,
            PropertySourceVersion: 1,
            PropertyDetailsVersion: 1,
            PropertyAvailabilitySelectionVersion: 1,
            PropertyStatus: 1,
            RoomId: RoomId,
            RoomName: "101",
            RoomSourceVersion: 1,
            RoomDetailsVersion: 1,
            RoomStatus: 1,
            RoomSalesMode: 1,
            RoomConfigurationVersion: 1,
            RoomAvailabilityMutationVersion: availabilityVersion,
            UnitSourceVersion: 1,
            UnitDetailsVersion: 1,
            UnitTopologyActive: true,
            UnitAvailabilityMutationVersion: availabilityVersion,
            Retirements: []);
        return new([unit], [coordinate], IsTruncated: false);
    }

    private static InventoryBlockTargetResolution BoundaryResolution(int count)
    {
        InventoryUnitSnapshot[] units = new InventoryUnitSnapshot[count];
        InventoryBlockSelectionCoordinate[] coordinates =
            new InventoryBlockSelectionCoordinate[count];
        for (int index = 0; index < count; index++)
        {
            Guid unitId = Guid.Parse(
                $"{(index + 1).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)}-0000-0000-0000-000000000001");
            units[index] = new(
                new(
                    unitId,
                    PropertyId,
                    RoomId,
                    BedId: null,
                    InventoryUnitKind.Room,
                    $"unit-{index}",
                    IsSellable: true,
                    IsTopologyActive: true),
                IsSellable: true);
            coordinates[index] = new(
                unitId,
                PropertySourceVersion: 1,
                PropertyDetailsVersion: 1,
                PropertyAvailabilitySelectionVersion: 1,
                PropertyStatus: 1,
                RoomId: RoomId,
                RoomName: "101",
                RoomSourceVersion: 1,
                RoomDetailsVersion: 1,
                RoomStatus: 1,
                RoomSalesMode: 1,
                RoomConfigurationVersion: 1,
                RoomAvailabilityMutationVersion: 1,
                UnitSourceVersion: 1,
                UnitDetailsVersion: 1,
                UnitTopologyActive: true,
                UnitAvailabilityMutationVersion: 1,
                Retirements: []);
        }

        return new(
            units,
            coordinates,
            IsTruncated: count >
                InventoryContractLimits.MaximumManualInventoryBlockGroupMembers);
    }

    private static ManualInventoryBlockGroup CreateGroup() => ManualInventoryBlockGroup.Create(
        Guid.NewGuid(),
        "tenant-a",
        PropertyId,
        ManualInventoryBlockGroupTargetKind.Property,
        buildingLabel: null,
        floorLabel: null,
        roomId: null,
        inventoryUnitId: null,
        Arrival,
        Departure,
        "Maintenance",
        new string('a', 64),
        new string('b', 64),
        ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
        initialBlockCount: 1,
        replacesGroupId: null,
        new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero),
        "user:operator").Value;

    private sealed class SequenceInventoryReadRepository(params InventoryBlockTargetResolution[] resolutions)
        : IInventoryReadRepository
    {
        private readonly Queue<InventoryBlockTargetResolution> resolutions = new(resolutions);

        public int ResolveCount { get; private set; }
        public List<int> MaximumUnitCounts { get; } = [];

        public Task<bool> PropertyExistsAsync(Guid propertyId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<InventoryBlockTargetResolution> ResolveBlockTargetUnitsBoundedAsync(
            Guid propertyId,
            InventoryBlockTarget target,
            int maximumUnitCount,
            CancellationToken cancellationToken)
        {
            this.ResolveCount++;
            this.MaximumUnitCounts.Add(maximumUnitCount);
            return Task.FromResult(this.resolutions.Dequeue());
        }

        public Task<RoomInventoryDto?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryUnitSnapshot?> GetUnitAsync(Guid propertyId, Guid inventoryUnitId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<InventoryUnitSnapshot>> ResolveBlockTargetUnitsAsync(
            Guid propertyId,
            InventoryBlockTarget target,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RoomInventoryListResponse> ListRoomsAsync(
            Guid propertyId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InventoryAvailabilityResponse> GetAvailabilityAsync(
            Guid propertyId,
            DateOnly arrival,
            DateOnly departure,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingAvailabilityRepository : IInventoryAvailabilityRepository
    {
        public int TouchCount { get; private set; }
        public long TrackedFenceVersion { get; private set; }
        public int ConflictReadCount { get; private set; }

        public Task TouchUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken)
        {
            this.TouchCount++;
            this.TrackedFenceVersion++;
            return Task.CompletedTask;
        }

        public Task<InventoryAvailabilityContextSnapshot> GetContextAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => Task.FromResult(
                new InventoryAvailabilityContextSnapshot([], inventoryUnitIds));

        public Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> conflictUnitIds,
            DateOnly arrival,
            DateOnly departure,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken)
        {
            this.ConflictReadCount++;
            return Task.FromResult(
                new InventoryAvailabilityConflictSnapshot(false, false));
        }
    }

    private sealed class RecordingGroupRepository(
        ManualInventoryBlockGroup? existing = null)
        : IManualInventoryBlockGroupRepository
    {
        public List<ManualInventoryBlockGroup> Added { get; } = [];

        public Task AddAsync(
            ManualInventoryBlockGroup blockGroup,
            CancellationToken cancellationToken)
        {
            this.Added.Add(blockGroup);
            return Task.CompletedTask;
        }

        public Task<ManualInventoryBlockGroup?> GetAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => Task.FromResult(
                existing is not null &&
                existing.PropertyId == propertyId &&
                existing.Id == blockGroupId
                    ? existing
                    : null);

        public Task ReloadAsync(
            ManualInventoryBlockGroup blockGroup,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualInventoryBlockGroupDto?> GetDtoAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualInventoryBlockGroupListResponse> ListAsync(
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingBlockRepository(
        IReadOnlyCollection<ManualInventoryBlock>? existing = null)
        : IManualInventoryBlockRepository
    {
        public List<IReadOnlyCollection<ManualInventoryBlock>> AddedBatches { get; } = [];

        public Task AddRangeAsync(
            IReadOnlyCollection<ManualInventoryBlock> blocks,
            CancellationToken cancellationToken)
        {
            this.AddedBatches.Add(blocks);
            return Task.CompletedTask;
        }

        public Task<ManualInventoryBlockIdentity?> GetIdentityAsync(
            Guid propertyId,
            Guid blockId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(
            ManualInventoryBlock block,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualInventoryBlock?> GetAsync(
            Guid propertyId,
            Guid blockId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<ManualInventoryBlock>> GetActiveGroupAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<ManualInventoryBlock>>(
                existing?.Where(block =>
                        block.PropertyId == propertyId &&
                        block.BlockGroupId == blockGroupId &&
                        block.Status == ManualInventoryBlockState.Active)
                    .ToArray() ?? []);

        public Task<ManualInventoryBlockGroupMemberListResponse> ListGroupMembersAsync(
            Guid propertyId,
            Guid blockGroupId,
            ManualInventoryBlockStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualInventoryBlockListResponse> ListAsync(
            Guid propertyId,
            Guid? inventoryUnitId,
            bool includeReleased,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingOperationRepository
        : IInventoryManagementOperationRepository
    {
        private readonly Dictionary<
            (InventoryManagementResourceKind Kind, Guid ResourceId, Guid OperationId),
            InventoryManagementOperationRecord> operations = [];

        public List<InventoryManagementOperationRecord> Added { get; } = [];

        public Task<InventoryManagementOperationRecord?> GetAsync(
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.operations.TryGetValue(
                (resourceKind, resourceId, operationId),
                out InventoryManagementOperationRecord? operation);
            return Task.FromResult(operation);
        }

        public Task AddAsync(
            InventoryManagementOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.operations.Add(
                (operation.ResourceKind, operation.ResourceId, operation.OperationId),
                operation);
            this.Added.Add(operation);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLock : IInventoryManagementLock
    {
        public Task AcquireResourceAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireOperationAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingSelectionFence : IInventoryAvailabilitySelectionFence
    {
        public int AcquireCount { get; private set; }
        public int AdvanceCount { get; private set; }

        public Task AcquireAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AcquireCount++;
            return Task.CompletedTask;
        }

        public Task AdvanceAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AdvanceCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private int value;

        public Guid NewId()
        {
            this.value++;
            return Guid.Parse(
                $"90000000-0000-0000-0000-{this.value.ToString("x12", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    private sealed class ThrowingGroupRepository(
        ManualInventoryBlockGroup? group = null,
        bool throwFormatException = false)
        : IManualInventoryBlockGroupRepository
    {
        public Task<ManualInventoryBlockGroup?> GetAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => Task.FromResult(group);

        public Task<ManualInventoryBlockGroupListResponse> ListAsync(
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throwFormatException
                ? throw new FormatException("The cursor is malformed.")
                : throw new ArgumentException(
                    "The cursor coordinates do not match.",
                    nameof(cursor));

        public Task ReloadAsync(ManualInventoryBlockGroup blockGroup, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(ManualInventoryBlockGroup blockGroup, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManualInventoryBlockGroupDto?> GetDtoAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StaticGroupDtoRepository(ManualInventoryBlockGroupDto dto)
        : IManualInventoryBlockGroupRepository
    {
        public Task<ManualInventoryBlockGroupDto?> GetDtoAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => Task.FromResult<ManualInventoryBlockGroupDto?>(
                dto.PropertyId == propertyId && dto.BlockGroupId == blockGroupId
                    ? dto
                    : null);

        public Task<ManualInventoryBlockGroup?> GetAsync(
            Guid propertyId,
            Guid blockGroupId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReloadAsync(
            ManualInventoryBlockGroup blockGroup,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(
            ManualInventoryBlockGroup blockGroup,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualInventoryBlockGroupListResponse> ListAsync(
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingBlockRepository : IManualInventoryBlockRepository
    {
        public Task<ManualInventoryBlockGroupMemberListResponse> ListGroupMembersAsync(
            Guid propertyId,
            Guid blockGroupId,
            ManualInventoryBlockStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new ArgumentException(
                "The cursor coordinates do not match.",
                nameof(cursor));

        public Task<ManualInventoryBlockIdentity?> GetIdentityAsync(Guid propertyId, Guid blockId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(ManualInventoryBlock block, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddRangeAsync(IReadOnlyCollection<ManualInventoryBlock> blocks, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManualInventoryBlock?> GetAsync(Guid propertyId, Guid blockId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<ManualInventoryBlock>> GetActiveGroupAsync(Guid propertyId, Guid blockGroupId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManualInventoryBlockListResponse> ListAsync(
            Guid propertyId,
            Guid? inventoryUnitId,
            bool includeReleased,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
