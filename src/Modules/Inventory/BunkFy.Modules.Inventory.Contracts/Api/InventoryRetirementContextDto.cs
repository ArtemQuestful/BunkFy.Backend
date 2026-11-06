namespace BunkFy.Modules.Inventory.Contracts;

public sealed record InventoryRetirementContextDto(
    Guid PropertyId,
    Guid RoomId,
    Guid? BedId,
    bool IsTopologyActive,
    InventoryRetirementProcessSummaryDto? Process,
    InventoryRetirementImpactDto? Impact,
    InventoryRetirementImpactStatus ImpactStatus,
    bool CanRequest,
    bool CanRetry,
    bool CanCancel);

public sealed record InventoryRetirementProcessSummaryDto(
    Guid TopologyChangeId,
    InventoryRetirementStatus Status,
    long Version,
    string Reason,
    string RequestedBy,
    int? RejectionReasonCode,
    string? CancellationReason,
    string? CanceledBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CanceledAtUtc);

public sealed record InventoryRetirementImpactDto(
    int ActiveAllocationCount,
    int ActiveManualBlockCount,
    int ActiveBedRetirementCount,
    bool ParentRoomRetirementActive,
    IReadOnlyCollection<Guid> AffectedReservationIds,
    bool AffectedReservationIdsTruncated);

public enum InventoryRetirementImpactStatus
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}
