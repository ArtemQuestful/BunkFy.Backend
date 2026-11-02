namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class InventoryTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record InventoryUnitTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.room-id")]
    Guid RoomId,
    [property: InventoryTenantExportField("inventory.bed-id")]
    Guid? BedId,
    [property: InventoryTenantExportField("inventory.inventory-unit-id")]
    Guid InventoryUnitId,
    [property: InventoryTenantExportField("inventory.unit-kind")]
    InventoryUnitKind Kind,
    [property: InventoryTenantExportField("inventory.unit-label")]
    string Label,
    [property: InventoryTenantExportField("inventory.topology-active")]
    bool IsTopologyActive,
    [property: InventoryTenantExportField("inventory.source-version")]
    long SourceVersion,
    [property: InventoryTenantExportField("inventory.details-version")]
    long DetailsVersion,
    [property: InventoryTenantExportField("inventory.known")]
    bool IsKnown,
    [property: InventoryTenantExportField(
        "inventory.availability-mutation-version")]
    long AvailabilityMutationVersion);

internal sealed record InventoryRoomConfigurationTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.room-id")]
    Guid RoomId,
    [property: InventoryTenantExportField("inventory.sales-mode")]
    RoomSalesMode SalesMode,
    [property: InventoryTenantExportField("inventory.version")]
    long Version,
    [property: InventoryTenantExportField(
        "inventory.availability-mutation-version")]
    long AvailabilityMutationVersion,
    [property: InventoryTenantExportField("inventory.created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: InventoryTenantExportField("inventory.updated-at")]
    DateTimeOffset? UpdatedAtUtc);

internal sealed record InventoryManagementOperationTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.operation-id")]
    Guid OperationId,
    [property: InventoryTenantExportField(
        "inventory.management-operation")]
    InventoryManagementOperationStateTenantExport State);

internal sealed record InventoryManagementOperationStateTenantExport(
    Application.Ports
        .InventoryManagementResourceKind ResourceKind,
    Guid ResourceId,
    Application.Ports
        .InventoryManagementMutationKind Kind,
    long ExpectedVersion,
    string RequestFingerprint,
    InventorySalesMode? ResultSalesMode,
    Guid? ResultBlockId,
    Guid? ResultBlockGroupId,
    ManualInventoryBlockStatus? ResultBlockStatus,
    int? ResultAffectedBlockCount,
    Guid? ResultTopologyChangeId,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc,
    ManualInventoryBlockGroupStatus? ResultBlockGroupStatus,
    Guid? ResultPreviousBlockGroupId,
    int? ResultTotalBlockCount,
    int? ResultActiveBlockCount,
    int? ResultReleasedBlockCount,
    int? ResultAlreadyReleasedBlockCount,
    int? ResultCreatedBlockCount,
    string? ResultMembershipDigest);

internal sealed record InventoryManualBlockTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.block-id")]
    Guid BlockId,
    [property: InventoryTenantExportField("inventory.block-group-id")]
    Guid BlockGroupId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.inventory-unit-id")]
    Guid InventoryUnitId,
    [property: InventoryTenantExportField("inventory.arrival")]
    DateOnly Arrival,
    [property: InventoryTenantExportField("inventory.departure")]
    DateOnly Departure,
    [property: InventoryTenantExportField("inventory.operational-reason")]
    string Reason,
    [property: InventoryTenantExportField("inventory.state")]
    ManualInventoryBlockState Status,
    [property: InventoryTenantExportField("inventory.version")]
    long Version,
    [property: InventoryTenantExportField("inventory.created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: InventoryTenantExportField("inventory.released-at")]
    DateTimeOffset? ReleasedAtUtc);

internal sealed record InventoryManualBlockGroupTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.block-group-id")]
    Guid BlockGroupId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.staff-change-operations")]
    InventoryManualBlockGroupTenantExportState State,
    [property: InventoryTenantExportField("inventory.operational-reason")]
    string Reason,
    [property: InventoryTenantExportField("inventory.staff-actor-reference")]
    InventoryManualBlockGroupActorTenantExport Actors);

internal sealed record InventoryManualBlockGroupActorTenantExport(
    string? CreatedByActorId,
    string? LastModifiedByActorId);

internal sealed record InventoryManualBlockGroupTenantExportState(
    ManualInventoryBlockGroupTargetKind TargetKind,
    string? BuildingLabel,
    string? FloorLabel,
    Guid? RoomId,
    Guid? InventoryUnitId,
    DateOnly Arrival,
    DateOnly Departure,
    string? SelectionDigest,
    string MembershipDigest,
    int MembershipDigestVersion,
    int InitialBlockCount,
    int ActiveBlockCount,
    ManualInventoryBlockGroupState Status,
    long Version,
    Guid? ReplacesGroupId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? ReleasedAtUtc);

internal sealed record InventoryAllocationTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField(
        "inventory.guest-reservation-reference")]
    Guid ReservationId,
    [property: InventoryTenantExportField(
        "inventory.guest-allocation-operations")]
    InventoryAllocationTenantExportPayload Payload);

internal sealed record InventoryAllocationTenantExportPayload(
    Guid AllocationId,
    Guid AllocationRequestId,
    DateOnly Arrival,
    DateOnly Departure,
    InventoryAllocationState Status,
    InventoryAllocationRejection Rejection,
    long Version,
    Guid? ReleaseRequestId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    bool IsAnonymised,
    DateTimeOffset? AnonymisedAtUtc);

internal sealed record InventoryAllocationUnitTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField(
        "inventory.guest-allocation-operations")]
    InventoryAllocationUnitTenantExportPayload Payload);

internal sealed record InventoryAllocationUnitTenantExportPayload(
    Guid AllocationId,
    Guid InventoryUnitId);

internal sealed record InventoryAllocationAmendmentDecisionTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField(
        "inventory.guest-reservation-reference")]
    Guid ReservationId,
    [property: InventoryTenantExportField(
        "inventory.guest-allocation-operations")]
    InventoryAllocationAmendmentDecisionTenantExportPayload Payload);

internal sealed record
    InventoryAllocationAmendmentDecisionTenantExportPayload(
        Guid AmendmentRequestId,
        Guid AllocationId,
        string RequestFingerprint,
        bool Confirmed,
        InventoryAllocationRejectionReason? RejectionReason,
        long? AllocationVersion,
        DateTimeOffset DecidedAtUtc);

internal sealed record InventoryAnonymisationReceiptTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField(
        "inventory.allocation-anonymisation-proof")]
    InventoryAnonymisationReceiptProofTenantExport Proof,
    [property: InventoryTenantExportField(
        "inventory.staff-actor-reference")]
    string ActorId);

internal sealed record InventoryAnonymisationReceiptProofTenantExport(
    int ContractVersion,
    Guid ReceiptId,
    Guid WorkItemId,
    Guid IdempotencyKey,
    Guid PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    Guid AllocationId,
    long SelectedAllocationVersion,
    long ResultingAllocationVersion,
    Guid ResultingReservationPseudonym,
    Domain.DataRights
        .InventoryAllocationAnonymisationDisposition Disposition,
    Domain.DataRights
        .InventoryAllocationAnonymisationReason Reason,
    int RemovedAmendmentDecisionCount,
    string ApprovalEvidenceSha256,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record InventoryAnonymisationTombstoneTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField(
        "inventory.allocation-anonymisation-proof")]
    InventoryAnonymisationTombstoneProofTenantExport Proof);

internal sealed record InventoryAnonymisationTombstoneProofTenantExport(
    int ContractVersion,
    long Revision,
    Guid PropertyId,
    Guid AllocationId,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingAllocationVersion,
    Guid ResultingReservationPseudonym,
    bool AllocationPresent,
    DateTimeOffset CompletedAtUtc,
    Guid? LedgerEntryId,
    DateTimeOffset? LastReplayedAtUtc);

internal sealed record InventoryAnonymisationRestoreReceiptTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField(
        "inventory.allocation-anonymisation-proof")]
    InventoryAnonymisationRestoreReceiptProofTenantExport Proof);

internal sealed record InventoryAnonymisationRestoreReceiptProofTenantExport(
    int ContractVersion,
    Guid LedgerEntryId,
    long TenantSequence,
    string LedgerEntrySha256,
    Guid PropertyId,
    Guid AllocationId,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingAllocationVersion,
    Guid ResultingReservationPseudonym,
    bool AllocationPresent,
    DateTimeOffset OriginallyCompletedAtUtc,
    long TombstoneRevision,
    DateTimeOffset ReplayedAtUtc,
    string CanonicalSha256);

internal sealed record InventoryBedRetirementTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.retirement-id")]
    Guid RetirementId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.room-id")]
    Guid RoomId,
    [property: InventoryTenantExportField("inventory.bed-id")]
    Guid BedId,
    [property: InventoryTenantExportField("inventory.operational-reason")]
    string Reason,
    [property: InventoryTenantExportField(
        "inventory.staff-actor-reference")]
    string RequestedBy,
    [property: InventoryTenantExportField("inventory.cancellation-reason")]
    string? CancellationReason,
    [property: InventoryTenantExportField(
        "inventory.cancellation-actor-reference")]
    string? CanceledBy,
    [property: InventoryTenantExportField("inventory.canceled-at")]
    DateTimeOffset? CanceledAtUtc,
    [property: InventoryTenantExportField("inventory.state")]
    InventoryRetirementProcessState State,
    [property: InventoryTenantExportField(
        "inventory.rejection-reason-code")]
    int? RejectionReasonCode,
    [property: InventoryTenantExportField("inventory.version")]
    long Version,
    [property: InventoryTenantExportField("inventory.created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: InventoryTenantExportField("inventory.updated-at")]
    DateTimeOffset? UpdatedAtUtc,
    [property: InventoryTenantExportField("inventory.completed-at")]
    DateTimeOffset? CompletedAtUtc);

internal sealed record InventoryRoomRetirementTenantExport(
    [property: InventoryTenantExportField("inventory.scope-id")]
    string ScopeId,
    [property: InventoryTenantExportField("inventory.retirement-id")]
    Guid RetirementId,
    [property: InventoryTenantExportField("inventory.property-id")]
    Guid PropertyId,
    [property: InventoryTenantExportField("inventory.room-id")]
    Guid RoomId,
    [property: InventoryTenantExportField("inventory.operational-reason")]
    string Reason,
    [property: InventoryTenantExportField(
        "inventory.staff-actor-reference")]
    string RequestedBy,
    [property: InventoryTenantExportField("inventory.cancellation-reason")]
    string? CancellationReason,
    [property: InventoryTenantExportField(
        "inventory.cancellation-actor-reference")]
    string? CanceledBy,
    [property: InventoryTenantExportField("inventory.canceled-at")]
    DateTimeOffset? CanceledAtUtc,
    [property: InventoryTenantExportField("inventory.state")]
    InventoryRetirementProcessState State,
    [property: InventoryTenantExportField(
        "inventory.rejection-reason-code")]
    int? RejectionReasonCode,
    [property: InventoryTenantExportField("inventory.version")]
    long Version,
    [property: InventoryTenantExportField("inventory.created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: InventoryTenantExportField("inventory.updated-at")]
    DateTimeOffset? UpdatedAtUtc,
    [property: InventoryTenantExportField("inventory.completed-at")]
    DateTimeOffset? CompletedAtUtc);
