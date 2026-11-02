namespace BunkFy.Modules.Inventory.Application;

using BunkFy.Modules.Inventory.Domain.Errors;
using Gma.Framework.Results;

public static class InventoryApplicationErrors
{
    public static readonly Error AccessDenied = new("Inventory.AccessDenied", "The subject cannot access the requested inventory scope.");
    public static readonly Error ConfirmationRequired = new(
        "Inventory.ConfirmationRequired",
        "Confirmation is required.");
    public static readonly Error TenantRequired = new("Inventory.TenantRequired", "A tenant context is required.");
    public static readonly Error PropertyNotFound = new("Inventory.PropertyNotFound", "The inventory property was not found.");
    public static Error RoomNotFound => InventoryDomainErrors.RoomNotFound;
    public static Error RoomRetired => InventoryDomainErrors.RoomRetired;
    public static Error BedLevelRequiresBeds => InventoryDomainErrors.BedLevelRequiresBeds;
    public static Error SalesModeInvalid => InventoryDomainErrors.SalesModeInvalid;
    public static Error VersionConflict => InventoryDomainErrors.VersionConflict;
    public static readonly Error RoomHasActiveClaims = new(
        "Inventory.RoomHasActiveClaims",
        "The room sales mode cannot change while active reservations or inventory blocks depend on it.");
    public static readonly Error InventoryUnitNotFound = new("Inventory.InventoryUnitNotFound", "The inventory unit was not found.");
    public static readonly Error InventoryUnitInactive = new("Inventory.InventoryUnitInactive", "The inventory unit topology is inactive.");
    public static readonly Error InventoryUnitNotSellable = new("Inventory.InventoryUnitNotSellable", "The inventory unit is not sellable in the room's current mode.");
    public static readonly Error BlockNotFound = new("Inventory.BlockNotFound", "The inventory block was not found.");
    public static readonly Error BlockGroupNotFound = new("Inventory.BlockGroupNotFound", "The inventory block group was not found.");
    public static readonly Error BlockGroupOperationNotFound = new("Inventory.BlockGroupOperationNotFound", "The manual inventory block group operation was not found.");
    public static readonly Error BlockGroupConfirmationRequired = new("Inventory.BlockGroupConfirmationRequired", "Explicit confirmation is required for the manual inventory block group mutation.");
    public static readonly Error BlockGroupSelectionDigestInvalid = new("Inventory.BlockGroupSelectionDigestInvalid", "The expected manual inventory block group selection digest must be a lowercase SHA-256 value.");
    public static readonly Error BlockGroupSelectionMismatch = new("Inventory.BlockGroupSelectionMismatch", "The selected inventory changed after preview. Preview again and confirm the current selection.");
    public static readonly Error BlockGroupTargetTooLarge = new("Inventory.BlockGroupTargetTooLarge", "The manual inventory block group target exceeds the 500-member atomic limit.");
    public static readonly Error BlockGroupCursorInvalid = new("Inventory.BlockGroupCursorInvalid", "The manual inventory block group cursor is invalid.");
    public static readonly Error BlockTargetInvalid = new("Inventory.BlockTargetInvalid", "The inventory block target is invalid.");
    public static readonly Error BlockTargetEmpty = new("Inventory.BlockTargetEmpty", "The selected target has no sellable inventory.");
    public static readonly Error BlockOverlap = new("Inventory.BlockOverlap", "An active manual block already overlaps this stay range.");
    public static readonly Error BlockAllocationConflict = new("Inventory.BlockAllocationConflict", "An active allocation overlaps this stay range.");
    public static Error StayRangeInvalid => InventoryDomainErrors.StayRangeInvalid;
    public static Error BlockReasonInvalid => InventoryDomainErrors.BlockReasonInvalid;
    public static Error BlockAlreadyReleased => InventoryDomainErrors.BlockAlreadyReleased;
    public static readonly Error BedRetirementNotFound = new("Inventory.BedRetirementNotFound", "The bed retirement process was not found.");
    public static readonly Error BedRetirementRetryInvalid = new("Inventory.BedRetirementRetryInvalid", "Only a rejected bed retirement can be retried.");
    public static readonly Error BedRetirementStillDraining = new("Inventory.BedRetirementStillDraining", "Active reservations or inventory blocks still depend on this bed.");
    public static readonly Error BedRetirementInProgress = new("Inventory.BedRetirementInProgress", "A bed retirement must finish before the room can be retired.");
    public static readonly Error RoomRetirementNotFound = new("Inventory.RoomRetirementNotFound", "The room retirement process was not found.");
    public static readonly Error RoomRetirementRetryInvalid = new("Inventory.RoomRetirementRetryInvalid", "Only a rejected room retirement can be retried.");
    public static readonly Error RoomRetirementStillDraining = new("Inventory.RoomRetirementStillDraining", "Active reservations, inventory blocks, or bed retirements still depend on this room.");
    public static readonly Error RoomRetirementInProgress = new("Inventory.RoomRetirementInProgress", "The room is already being retired, so its beds cannot be retired independently.");
    public static readonly Error WorkspaceProcessingRestricted = new(
        "Inventory.WorkspaceProcessingRestricted",
        "The workspace is not accepting operational changes.");
    public static readonly Error WorkspaceProcessingAdmissionUnavailable = new(
        "Inventory.WorkspaceProcessingAdmissionUnavailable",
        "Workspace processing admission is temporarily unavailable.");
    public static readonly Error ManagementOperationInvalid = new(
        "Inventory.ManagementOperationInvalid",
        "A valid Inventory management operation id is required.");
    public static readonly Error ManagementOperationConflict = new(
        "Inventory.ManagementOperationConflict",
        "The Inventory management operation id is already bound to a different request.");
    public static readonly Error RetirementRequestConflict = new(
        "Inventory.RetirementRequestConflict",
        "A retirement process already exists for this target with a different reason.");
    public static readonly Error AnonymisationRequestInvalid = new(
        "Inventory.AnonymisationRequestInvalid",
        "The inventory allocation anonymisation request is invalid.");
    public static readonly Error DataRightsApprovalRequired = new(
        "Inventory.DataRightsApprovalRequired",
        "The inventory allocation operation requires a current approved data-rights case.");
    public static readonly Error AllocationNotFound = new(
        "Inventory.AllocationNotFound",
        "The inventory allocation was not found.");
    public static readonly Error AnonymisationIdempotencyConflict = new(
        "Inventory.AnonymisationIdempotencyConflict",
        "The anonymisation idempotency key is bound to a different inventory allocation request.");
    public static readonly Error AnonymisationProofUnavailable = new(
        "Inventory.AnonymisationProofUnavailable",
        "The committed inventory allocation anonymisation result cannot be proven.");
    public static readonly Error AnonymisationRestoreRequestInvalid = new(
        "Inventory.AnonymisationRestoreRequestInvalid",
        "The inventory allocation anonymisation restore request is invalid.");
    public static readonly Error AnonymisationRestoreProofConflict = new(
        "Inventory.AnonymisationRestoreProofConflict",
        "The restored inventory allocation state conflicts with the protected anonymisation proof.");
    public static Error AnonymisationBlocked(string reasonCode) => new(
        $"Inventory.AnonymisationBlocked.{reasonCode}",
        "The inventory allocation cannot be anonymised under its current owner state.");
}
