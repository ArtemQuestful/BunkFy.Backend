namespace BunkFy.Modules.Inventory.Persistence;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Domain;

internal sealed class InventoryManagementOperation : IScopedEntity
{
    private InventoryManagementOperation() { }

    internal InventoryManagementOperation(
        InventoryManagementOperationRecord record)
    {
        this.Id = record.OperationId;
        this.ScopeId = record.ScopeId;
        this.PropertyId = record.PropertyId;
        this.ResourceKind = record.ResourceKind;
        this.ResourceId = record.ResourceId;
        this.Kind = record.Kind;
        this.ExpectedVersion = record.ExpectedVersion;
        this.RequestFingerprint = record.RequestFingerprint;
        this.ResultSalesMode = record.ResultSalesMode;
        this.ResultBlockId = record.ResultBlockId;
        this.ResultBlockGroupId = record.ResultBlockGroupId;
        this.ResultBlockStatus = record.ResultBlockStatus;
        this.ResultAffectedBlockCount = record.ResultAffectedBlockCount;
        this.ResultVersion = record.ResultVersion;
        this.ResultTopologyChangeId = record.ResultTopologyChangeId;
        this.ResultBlockGroupStatus = record.ResultBlockGroupStatus;
        this.ResultPreviousBlockGroupId = record.ResultPreviousBlockGroupId;
        this.ResultTotalBlockCount = record.ResultTotalBlockCount;
        this.ResultActiveBlockCount = record.ResultActiveBlockCount;
        this.ResultReleasedBlockCount = record.ResultReleasedBlockCount;
        this.ResultAlreadyReleasedBlockCount =
            record.ResultAlreadyReleasedBlockCount;
        this.ResultCreatedBlockCount = record.ResultCreatedBlockCount;
        this.ResultMembershipDigest = record.ResultMembershipDigest;
        this.CompletedAtUtc = record.CompletedAtUtc;
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid PropertyId { get; private set; }
    public InventoryManagementResourceKind ResourceKind { get; private set; }
    public Guid ResourceId { get; private set; }
    public InventoryManagementMutationKind Kind { get; private set; }
    public long ExpectedVersion { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public InventorySalesMode? ResultSalesMode { get; private set; }
    public Guid? ResultBlockId { get; private set; }
    public Guid? ResultBlockGroupId { get; private set; }
    public ManualInventoryBlockStatus? ResultBlockStatus { get; private set; }
    public int? ResultAffectedBlockCount { get; private set; }
    public long ResultVersion { get; private set; }
    public Guid? ResultTopologyChangeId { get; private set; }
    public ManualInventoryBlockGroupStatus? ResultBlockGroupStatus
    {
        get;
        private set;
    }
    public Guid? ResultPreviousBlockGroupId { get; private set; }
    public int? ResultTotalBlockCount { get; private set; }
    public int? ResultActiveBlockCount { get; private set; }
    public int? ResultReleasedBlockCount { get; private set; }
    public int? ResultAlreadyReleasedBlockCount { get; private set; }
    public int? ResultCreatedBlockCount { get; private set; }
    public string? ResultMembershipDigest { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }

    internal InventoryManagementOperationRecord ToRecord() => new(
        this.Id,
        this.ScopeId,
        this.PropertyId,
        this.ResourceKind,
        this.ResourceId,
        this.Kind,
        this.ExpectedVersion,
        this.RequestFingerprint,
        this.ResultSalesMode,
        this.ResultBlockId,
        this.ResultBlockGroupId,
        this.ResultBlockStatus,
        this.ResultAffectedBlockCount,
        this.ResultVersion,
        this.CompletedAtUtc,
        this.ResultTopologyChangeId,
        this.ResultBlockGroupStatus,
        this.ResultPreviousBlockGroupId,
        this.ResultTotalBlockCount,
        this.ResultActiveBlockCount,
        this.ResultReleasedBlockCount,
        this.ResultAlreadyReleasedBlockCount,
        this.ResultCreatedBlockCount,
        this.ResultMembershipDigest);
}
