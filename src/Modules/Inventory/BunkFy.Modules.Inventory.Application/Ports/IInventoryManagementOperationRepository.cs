namespace BunkFy.Modules.Inventory.Application.Ports;

using BunkFy.Modules.Inventory.Contracts;

public interface IInventoryManagementOperationRepository
{
    Task<InventoryManagementOperationRecord?> GetAsync(
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task AddAsync(
        InventoryManagementOperationRecord operation,
        CancellationToken cancellationToken);
}

public sealed record InventoryManagementOperationRecord(
    Guid OperationId,
    string ScopeId,
    Guid PropertyId,
    InventoryManagementResourceKind ResourceKind,
    Guid ResourceId,
    InventoryManagementMutationKind Kind,
    long ExpectedVersion,
    string RequestFingerprint,
    InventorySalesMode? ResultSalesMode,
    Guid? ResultBlockId,
    Guid? ResultBlockGroupId,
    ManualInventoryBlockStatus? ResultBlockStatus,
    int? ResultAffectedBlockCount,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc,
    Guid? ResultTopologyChangeId = null,
    ManualInventoryBlockGroupStatus? ResultBlockGroupStatus = null,
    Guid? ResultPreviousBlockGroupId = null,
    int? ResultTotalBlockCount = null,
    int? ResultActiveBlockCount = null,
    int? ResultReleasedBlockCount = null,
    int? ResultAlreadyReleasedBlockCount = null,
    int? ResultCreatedBlockCount = null,
    string? ResultMembershipDigest = null)
{
    public bool Matches(
        InventoryManagementMutationKind kind,
        Guid propertyId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        long expectedVersion,
        string requestFingerprint) =>
        this.Kind == kind &&
        this.PropertyId == propertyId &&
        this.ResourceKind == resourceKind &&
        this.ResourceId == resourceId &&
        this.ExpectedVersion == expectedVersion &&
        string.Equals(
            this.RequestFingerprint,
            requestFingerprint,
            StringComparison.Ordinal);

    public RoomInventoryMutationReceiptDto ToRoomReceipt()
    {
        if (this.ResultSalesMode is null)
        {
            throw new InvalidDataException(
                "The Inventory management operation has no Room receipt.");
        }

        return new(
            this.PropertyId,
            this.ResourceId,
            this.ResultSalesMode.Value,
            this.ResultVersion);
    }

    public ManualInventoryBlockMutationReceiptDto ToBlockReceipt()
    {
        if (this.ResultBlockId is null ||
            this.ResultBlockGroupId is null ||
            this.ResultBlockStatus is null)
        {
            throw new InvalidDataException(
                "The Inventory management operation has no Block receipt.");
        }

        return new(
            this.ResultBlockId.Value,
            this.ResultBlockGroupId.Value,
            this.PropertyId,
            this.ResultBlockStatus.Value,
            this.ResultVersion);
    }

    public ManualInventoryBlockGroupMutationReceiptDto ToBlockGroupReceipt()
    {
        if (this.ResultBlockGroupId is null ||
            this.ResultAffectedBlockCount is null)
        {
            throw new InvalidDataException(
                "The Inventory management operation has no Block Group receipt.");
        }

        return new(
            this.ResultBlockGroupId.Value,
            this.PropertyId,
            this.ResultAffectedBlockCount.Value,
            this.ResultBlockGroupStatus,
            this.Kind is InventoryManagementMutationKind.ManualBlockGroupCreateV2 or
                InventoryManagementMutationKind.ManualBlockGroupReplace or
                InventoryManagementMutationKind.ManualBlockGroupReleaseV2
                ? this.ResultVersion
                : null,
            this.ResultPreviousBlockGroupId,
            this.ResultReleasedBlockCount,
            this.ResultCreatedBlockCount,
            this.ResultTotalBlockCount,
            this.ResultActiveBlockCount,
            this.ResultAlreadyReleasedBlockCount,
            this.ResultMembershipDigest);
    }

    public InventoryRetirementOperationPointer ToRetirementPointer()
    {
        if (this.ResultTopologyChangeId is null)
        {
            throw new InvalidDataException(
                "The Inventory management operation has no retirement pointer.");
        }

        return new(
            this.PropertyId,
            this.ResultTopologyChangeId.Value,
            this.ResultVersion);
    }

    public static InventoryManagementOperationRecord ForRoom(
        Guid operationId,
        string scopeId,
        Guid propertyId,
        Guid roomId,
        long expectedVersion,
        string requestFingerprint,
        RoomInventoryMutationReceiptDto receipt,
        DateTimeOffset completedAtUtc) => new(
            operationId,
            scopeId,
            propertyId,
            InventoryManagementResourceKind.Room,
            roomId,
            InventoryManagementMutationKind.RoomSalesModeConfiguration,
            expectedVersion,
            requestFingerprint,
            receipt.SalesMode,
            null,
            null,
            null,
            null,
            receipt.Version,
            completedAtUtc);

    public static InventoryManagementOperationRecord ForBlock(
        Guid operationId,
        string scopeId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        string requestFingerprint,
        ManualInventoryBlockMutationReceiptDto receipt,
        DateTimeOffset completedAtUtc) => new(
            operationId,
            scopeId,
            receipt.PropertyId,
            resourceKind,
            resourceId,
            kind,
            expectedVersion,
            requestFingerprint,
            null,
            receipt.BlockId,
            receipt.BlockGroupId,
            receipt.Status,
            1,
            receipt.Version,
            completedAtUtc);

    public static InventoryManagementOperationRecord ForBlockGroup(
        Guid operationId,
        string scopeId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        string requestFingerprint,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        DateTimeOffset completedAtUtc) => new(
            operationId,
            scopeId,
            receipt.PropertyId,
            resourceKind,
            resourceId,
            kind,
            0,
            requestFingerprint,
            null,
            null,
            receipt.BlockGroupId,
            null,
            receipt.AffectedBlockCount,
            0,
            completedAtUtc);

    public static InventoryManagementOperationRecord ForBlockGroupV2(
        Guid operationId,
        string scopeId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        string requestFingerprint,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        DateTimeOffset completedAtUtc)
    {
        if (receipt.Status is null || receipt.Version is null ||
            receipt.ReleasedNowBlockCount is null || receipt.CreatedNowBlockCount is null ||
            receipt.TotalBlockCount is null || receipt.ActiveBlockCount is null ||
            receipt.AlreadyReleasedBlockCount is null ||
            string.IsNullOrWhiteSpace(receipt.MembershipDigest))
        {
            throw new InvalidDataException(
                "A V2 block-group operation requires a complete rich receipt.");
        }

        return new(
            operationId,
            scopeId,
            receipt.PropertyId,
            resourceKind,
            resourceId,
            kind,
            expectedVersion,
            requestFingerprint,
            null,
            null,
            receipt.BlockGroupId,
            null,
            receipt.AffectedBlockCount,
            receipt.Version ?? throw new InvalidDataException(
                "A V2 block-group receipt requires a version."),
            completedAtUtc,
            ResultBlockGroupStatus: receipt.Status,
            ResultPreviousBlockGroupId: receipt.PreviousBlockGroupId,
            ResultTotalBlockCount: receipt.TotalBlockCount,
            ResultActiveBlockCount: receipt.ActiveBlockCount,
            ResultReleasedBlockCount: receipt.ReleasedNowBlockCount,
            ResultAlreadyReleasedBlockCount: receipt.AlreadyReleasedBlockCount,
            ResultCreatedBlockCount: receipt.CreatedNowBlockCount,
            ResultMembershipDigest: receipt.MembershipDigest);
    }

    public ManualInventoryBlockGroupOperationDto ToBlockGroupOperationDto() => new(
        this.OperationId,
        this.PropertyId,
        this.ResourceKind == InventoryManagementResourceKind.BlockGroup
            ? this.ResourceId
            : null,
        this.Kind switch
        {
            InventoryManagementMutationKind.ManualBlockGroupCreate or
                InventoryManagementMutationKind.ManualBlockGroupCreateV2 =>
                ManualInventoryBlockGroupOperationKind.Create,
            InventoryManagementMutationKind.ManualBlockGroupReplace =>
                ManualInventoryBlockGroupOperationKind.Replace,
            InventoryManagementMutationKind.ManualBlockGroupRelease or
                InventoryManagementMutationKind.ManualBlockGroupReleaseV2 =>
                ManualInventoryBlockGroupOperationKind.Release,
            _ => ManualInventoryBlockGroupOperationKind.Unknown
        },
        ManualInventoryBlockGroupOperationStatus.Applied,
        this.ToBlockGroupReceipt(),
        this.CompletedAtUtc);

    public static InventoryManagementOperationRecord ForRetirement(
        Guid operationId,
        string scopeId,
        Guid propertyId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        string requestFingerprint,
        Guid topologyChangeId,
        long resultVersion,
        DateTimeOffset completedAtUtc) => new(
            operationId,
            scopeId,
            propertyId,
            resourceKind,
            resourceId,
            kind,
            expectedVersion,
            requestFingerprint,
            null,
            null,
            null,
            null,
            null,
            resultVersion,
            completedAtUtc,
            topologyChangeId);
}

public sealed record InventoryRetirementOperationPointer(
    Guid PropertyId,
    Guid TopologyChangeId,
    long ResultVersion);

public enum InventoryManagementResourceKind
{
    Room = 1,
    Property = 2,
    Block = 3,
    BlockGroup = 4,
    InventoryUnit = 5,
    BedRetirement = 6,
    RoomRetirement = 7
}

public enum InventoryManagementMutationKind
{
    RoomSalesModeConfiguration = 1,
    ManualBlockCreate = 2,
    ManualBlockGroupCreate = 3,
    ManualBlockRelease = 4,
    ManualBlockGroupRelease = 5,
    BedRetirementRequest = 6,
    BedRetirementRetry = 7,
    RoomRetirementRequest = 8,
    RoomRetirementRetry = 9,
    BedRetirementCancellation = 10,
    RoomRetirementCancellation = 11,
    ManualBlockGroupCreateV2 = 12,
    ManualBlockGroupReplace = 13,
    ManualBlockGroupReleaseV2 = 14
}
