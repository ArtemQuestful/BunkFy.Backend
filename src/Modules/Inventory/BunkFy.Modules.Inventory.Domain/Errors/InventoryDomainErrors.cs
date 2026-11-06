namespace BunkFy.Modules.Inventory.Domain.Errors;

using Gma.Framework.Results;

public static class InventoryDomainErrors
{
    public static readonly Error PropertyIdRequired = new("Inventory.PropertyIdRequired", "Property id is required.");
    public static readonly Error RoomIdRequired = new("Inventory.RoomIdRequired", "Room id is required.");
    public static readonly Error InventoryUnitIdRequired = new("Inventory.InventoryUnitIdRequired", "Inventory unit id is required.");
    public static readonly Error BlockIdRequired = new("Inventory.BlockIdRequired", "Block id is required.");
    public static readonly Error BlockGroupIdRequired = new("Inventory.BlockGroupIdRequired", "Block group id is required.");
    public static readonly Error BlockGroupIdentityInvalid = new("Inventory.BlockGroupIdentityInvalid", "The manual inventory block group identity is invalid.");
    public static readonly Error BlockGroupTargetInvalid = new("Inventory.BlockGroupTargetInvalid", "The manual inventory block group target is invalid.");
    public static readonly Error BlockGroupSelectionInvalid = new("Inventory.BlockGroupSelectionInvalid", "The manual inventory block group selection must have a SHA-256 digest and contain between 1 and 500 members.");
    public static readonly Error BlockGroupMemberCountInvalid = new("Inventory.BlockGroupMemberCountInvalid", "The manual inventory block group member count is inconsistent with its active children.");
    public static readonly Error BlockGroupActorInvalid = new("Inventory.BlockGroupActorInvalid", "The manual inventory block group actor id is required and must be a control-free value of 200 characters or fewer.");
    public static readonly Error BlockGroupAlreadyTerminal = new("Inventory.BlockGroupAlreadyTerminal", "The manual inventory block group is already released or replaced.");
    public static readonly Error BlockGroupTimestampInvalid = new("Inventory.BlockGroupTimestampInvalid", "The manual inventory block group timestamp is invalid or moves backwards.");
    public static readonly Error RoomNotFound = new("Inventory.RoomNotFound", "The inventory room was not found.");
    public static readonly Error RoomRetired = new("Inventory.RoomRetired", "Retired room topology cannot be configured.");
    public static readonly Error BedLevelRequiresBeds = new("Inventory.BedLevelRequiresBeds", "Bed-level sales require at least one active bed.");
    public static readonly Error SalesModeInvalid = new("Inventory.SalesModeInvalid", "Sales mode must be room-level or bed-level.");
    public static readonly Error EventIdRequired = new("Inventory.EventIdRequired", "A domain event id is required.");
    public static readonly Error VersionConflict = new("Inventory.VersionConflict", "The inventory configuration has changed. Refresh and retry.");
    public static readonly Error StayRangeInvalid = new("Inventory.StayRangeInvalid", "Arrival must be before departure.");
    public static readonly Error BlockReasonInvalid = new("Inventory.BlockReasonInvalid", "Block reason is required and must be 500 characters or fewer.");
    public static readonly Error BlockAlreadyReleased = new("Inventory.BlockAlreadyReleased", "The inventory block is already released.");
    public static readonly Error AllocationIdRequired = new("Inventory.AllocationIdRequired", "Allocation id is required.");
    public static readonly Error ReservationIdRequired = new("Inventory.ReservationIdRequired", "Reservation id is required.");
    public static readonly Error AllocationRequestIdRequired = new("Inventory.AllocationRequestIdRequired", "Allocation request id is required.");
    public static readonly Error AllocationAmendmentRequestIdRequired = new("Inventory.AllocationAmendmentRequestIdRequired", "Allocation amendment request id is required.");
    public static readonly Error AllocationReleaseRequestIdRequired = new("Inventory.AllocationReleaseRequestIdRequired", "Allocation release request id is required.");
    public static readonly Error AllocationUnitsInvalid = new("Inventory.AllocationUnitsInvalid", "Allocation units must contain unique, non-empty ids within the supported limit.");
    public static readonly Error AllocationRejectionRequired = new("Inventory.AllocationRejectionRequired", "A rejected allocation requires a rejection reason.");
    public static readonly Error AllocationNotActive = new("Inventory.AllocationNotActive", "The inventory allocation is not active.");
    public static readonly Error AllocationAlreadyAnonymised = new(
        "Inventory.AllocationAlreadyAnonymised",
        "The inventory allocation is already anonymised.");
    public static readonly Error AllocationNotEligibleForAnonymisation = new(
        "Inventory.AllocationNotEligibleForAnonymisation",
        "Only released or rejected inventory allocations can be anonymised.");
    public static readonly Error AllocationAnonymisationProvenanceInvalid = new(
        "Inventory.AllocationAnonymisationProvenanceInvalid",
        "The inventory allocation anonymisation provenance is invalid.");
    public static readonly Error AllocationAnonymisationRestoreStateInvalid = new(
        "Inventory.AllocationAnonymisationRestoreStateInvalid",
        "The inventory allocation cannot restore the requested anonymised state.");
    public static readonly Error AllocationAnonymisationReceiptInvalid = new(
        "Inventory.AllocationAnonymisationReceiptInvalid",
        "The inventory allocation anonymisation receipt is invalid.");
    public static readonly Error AllocationAnonymisationTombstoneInvalid = new(
        "Inventory.AllocationAnonymisationTombstoneInvalid",
        "The inventory allocation anonymisation tombstone is invalid.");
    public static readonly Error AllocationAnonymisationRestoreReceiptInvalid = new(
        "Inventory.AllocationAnonymisationRestoreReceiptInvalid",
        "The inventory allocation anonymisation restore receipt is invalid.");
    public static readonly Error BedRetirementIdentityInvalid = new("Inventory.BedRetirementIdentityInvalid", "Bed retirement identity is invalid.");
    public static readonly Error BedRetirementRequestInvalid = new("Inventory.BedRetirementRequestInvalid", "Bed retirement reason and actor are required and must be within supported limits.");
    public static readonly Error BedRetirementCancellationRequestInvalid = new("Inventory.BedRetirementCancellationRequestInvalid", "Bed retirement cancellation reason and actor are required and must be within supported limits.");
    public static readonly Error BedRetirementTransitionInvalid = new("Inventory.BedRetirementTransitionInvalid", "The bed retirement process cannot perform this transition.");
    public static readonly Error RoomRetirementIdentityInvalid = new("Inventory.RoomRetirementIdentityInvalid", "Room retirement identity is invalid.");
    public static readonly Error RoomRetirementRequestInvalid = new("Inventory.RoomRetirementRequestInvalid", "Room retirement reason and actor are required and must be within supported limits.");
    public static readonly Error RoomRetirementCancellationRequestInvalid = new("Inventory.RoomRetirementCancellationRequestInvalid", "Room retirement cancellation reason and actor are required and must be within supported limits.");
    public static readonly Error RoomRetirementTransitionInvalid = new("Inventory.RoomRetirementTransitionInvalid", "The room retirement process cannot perform this transition.");
}
