namespace BunkFy.Modules.Reservations.Application;

using BunkFy.DataGovernance;
using Gma.Framework.Results;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Errors;

public static class ReservationsApplicationErrors
{
    public static Error CountryPolicyDenied(CountryPolicyDecisionReason reason) => new(
        $"Reservations.CountryPolicyDenied.{reason}",
        "The property is not enabled for this data-processing operation.");
    public static IReadOnlyList<Error> CountryPolicyDenials { get; } =
        Enum.GetValues<CountryPolicyDecisionReason>()
            .Where(reason => reason is not CountryPolicyDecisionReason.Unknown and not CountryPolicyDecisionReason.Allowed)
            .Select(CountryPolicyDenied)
            .ToArray();
    public static readonly Error ReservationNotFound = new("Reservations.ReservationNotFound", "The reservation was not found.");
    public static readonly Error PropertyNotFound = new(
        "Reservations.PropertyNotFound",
        "The property is not present in the Reservations projection.");
    public static readonly Error PropertyInactive = new(
        "Reservations.PropertyInactive",
        "The property is not active.");
    public static readonly Error PropertyTimeZoneUnavailable = new(
        "Reservations.PropertyTimeZoneUnavailable",
        "The property IANA time zone is unavailable or invalid.");
    public static readonly Error OperationsSnapshotLimitInvalid = new(
        "Reservations.OperationsSnapshotLimitInvalid",
        $"The upcoming limit must be between 0 and {ReservationsContractLimits.MaximumOperationsSnapshotUpcomingLimit}.");
    public static readonly Error OperationsSnapshotLocalDateInvalid = new(
        "Reservations.OperationsSnapshotLocalDateInvalid",
        "The local date must use yyyy-MM-dd format.");
    public static readonly Error ExternalSourceAlreadyExists = new("Reservations.ExternalSourceAlreadyExists", "A reservation already exists for this external source reference.");
    public static readonly Error CreationOperationConflict = new(
        "Reservations.CreationOperationConflict",
        "The reservation operation id is already bound to a different request.");
    public static readonly Error ManagementOperationInvalid = new(
        "Reservations.ManagementOperationInvalid",
        "The reservation lifecycle operation is invalid.");
    public static readonly Error ManagementOperationConflict = new(
        "Reservations.ManagementOperationConflict",
        "The reservation lifecycle operation id is already bound to a different request.");
    public static readonly Error TenantRequired = new("Reservations.TenantRequired", "A tenant context is required.");
    public static readonly Error WorkspaceProcessingRestricted = new(
        "Reservations.WorkspaceProcessingRestricted",
        "The workspace is not accepting reservation changes.");
    public static readonly Error WorkspaceProcessingAdmissionUnavailable = new(
        "Reservations.WorkspaceProcessingAdmissionUnavailable",
        "Workspace mutation admission is temporarily unavailable.");
    public static readonly Error InventoryUnitNotFound = new("Reservations.InventoryUnitNotFound", "One or more inventory units are not present in the local Inventory projection.");
    public static readonly Error InventoryUnitPropertyMismatch = new("Reservations.InventoryUnitPropertyMismatch", "One or more inventory units do not belong to the requested property.");
    public static readonly Error AllocationUnitMismatch = new(
        "Reservations.AllocationUnitMismatch",
        "The confirmed allocation does not match the reservation inventory request.");
    public static Error VersionConflict => ReservationsDomainErrors.VersionConflict;
    public static Error InvalidTransition => ReservationsDomainErrors.InvalidTransition;
    public static Error StayRangeInvalid => ReservationsDomainErrors.StayRangeInvalid;
    public static Error ExpectedStayTimeInvalid => ReservationsDomainErrors.ExpectedStayTimeInvalid;
    public static Error RequestedUnitsInvalid => ReservationsDomainErrors.RequestedUnitsInvalid;
    public static Error SourceInvalid => ReservationsDomainErrors.SourceInvalid;
    public static Error DetailsRevisionConflict => ReservationsDomainErrors.DetailsRevisionConflict;
    public static Error DetailsChangeProvenanceInvalid => ReservationsDomainErrors.DetailsChangeProvenanceInvalid;
    public static Error AllocationAmendmentInProgress => ReservationsDomainErrors.AllocationAmendmentInProgress;
    public static Error AllocationAmendmentInvalid => ReservationsDomainErrors.AllocationAmendmentInvalid;
    public static readonly Error StayAmendmentRequestInvalid = new(
        "Reservations.StayAmendmentRequestInvalid",
        "The stay-amendment request is invalid.");
    public static readonly Error StayAmendmentOperationNotFound = new(
        "Reservations.StayAmendmentOperationNotFound",
        "The stay-amendment operation was not found.");
    public static readonly Error StayAmendmentOperationConflict = new(
        "Reservations.StayAmendmentOperationConflict",
        "The stay-amendment operation id is already bound to a different request.");
    public static Error StayAmendmentOperationVersionConflict =>
        ReservationsDomainErrors.StayAmendmentOperationVersionConflict;
    public static readonly Error StayAmendmentReconcileInvalid = new(
        "Reservations.StayAmendmentReconcileInvalid",
        "Only a pending stay-amendment operation can be reconciled.");
    public static Error StayAmendmentReconcileTooSoon =>
        ReservationsDomainErrors.StayAmendmentReconcileTooSoon;
    public static Error StayBusinessDateInvalid => ReservationsDomainErrors.StayBusinessDateInvalid;
    public static Error StayProvenanceInvalid => ReservationsDomainErrors.StayProvenanceInvalid;
    public static readonly Error GuestNotLinkable = new("Reservations.GuestNotLinkable", "The guest is not active and visible at this property.");
    public static readonly Error ReminderTaskOptionsInvalid = new("Reservations.ReminderTaskOptionsInvalid", "Reservation reminder task options are invalid.");
    public static readonly Error GuestRecordLinkProcessNotFound = new(
        "Reservations.GuestRecordLinkProcessNotFound",
        "The Reservation Guest Record link process was not found.");
    public static readonly Error GuestRecordLinkProcessConflict = new(
        "Reservations.GuestRecordLinkProcessConflict",
        "The operation or Reservation is already bound to a different Guest Record link process.");
    public static readonly Error GuestRecordLinkProcessReservationOccupied = new(
        "Reservations.GuestRecordLinkProcessReservationOccupied",
        "The Reservation already has a current primary Guest Record.");
    public static readonly Error GuestRecordLinkProcessPending = new(
        "Reservations.GuestRecordLinkProcessPending",
        "The Guest Record link is waiting for current projection or restriction state.");
    public static readonly Error GuestRecordLinkProcessRetryInvalid = new(
        "Reservations.GuestRecordLinkProcessRetryInvalid",
        "Only a Reservation Guest Record link that needs review can be retried.");
    public static readonly Error GuestRecordLinkTaskInvalid = new(
        "Reservations.GuestRecordLinkTaskInvalid",
        "The Reservation Guest Record link task is invalid.");
    public static readonly Error CorrectionRequestInvalid = new(
        "Reservations.CorrectionRequestInvalid",
        "The reservation correction request is invalid.");
    public static readonly Error CorrectionIdempotencyConflict = new(
        "Reservations.CorrectionIdempotencyConflict",
        "The correction idempotency key is already bound to a different request or result.");
    public static readonly Error CorrectionNoChanges = new(
        "Reservations.CorrectionNoChanges",
        "The correction does not change the selected reservation details.");
    public static readonly Error DataRightsApprovalRequired = new(
        "Reservations.DataRightsApprovalRequired",
        "The reservation operation requires a current approved data-rights case.");
    public static readonly Error ProcessingRestrictionRequestInvalid = new(
        "Reservations.ProcessingRestrictionRequestInvalid",
        "The reservation processing-restriction request is invalid.");
    public static readonly Error ProcessingRestrictionIdempotencyConflict = new(
        "Reservations.ProcessingRestrictionIdempotencyConflict",
        "The processing-restriction idempotency key is bound to a different request.");
    public static readonly Error ProcessingRestrictionReservationVersionConflict = new(
        "Reservations.ProcessingRestrictionReservationVersionConflict",
        "The selected reservation version has changed.");
    public static readonly Error ProcessingRestrictionApprovalAlreadyUsed = new(
        "Reservations.ProcessingRestrictionApprovalAlreadyUsed",
        "The data-rights approval coordinate has already been used.");
    public static readonly Error ProcessingRestrictionNotFound = new(
        "Reservations.ProcessingRestrictionNotFound",
        "The reservation processing restriction was not found.");
    public static readonly Error ProcessingRestrictionProjectionUnavailable = new(
        "Reservations.ProcessingRestrictionProjectionUnavailable",
        "The reservation processing-restriction state is unavailable or unsupported.");
    public static readonly Error ProcessingRestrictionActiveStateInvalid = new(
        "Reservations.ProcessingRestrictionActiveStateInvalid",
        "The active reservation processing-restriction state is inconsistent.");
    public static readonly Error ProcessingRestrictionOwnerProofInvalid = new(
        "Reservations.ProcessingRestrictionOwnerProofInvalid",
        "The committed reservation processing-restriction proof is unavailable or inconsistent.");
    public static readonly Error DataHoldRequestInvalid = new(
        "Reservations.DataHoldRequestInvalid",
        "The reservation data-hold request is invalid.");
    public static readonly Error DataHoldIdempotencyConflict = new(
        "Reservations.DataHoldIdempotencyConflict",
        "The reservation data-hold idempotency key is bound to a different request.");
    public static readonly Error DataHoldReservationVersionConflict = new(
        "Reservations.DataHoldReservationVersionConflict",
        "The selected reservation version has changed.");
    public static readonly Error DataHoldDetailsRevisionConflict = new(
        "Reservations.DataHoldDetailsRevisionConflict",
        "The selected reservation details revision has changed.");
    public static readonly Error DataHoldNotFound = new(
        "Reservations.DataHoldNotFound",
        "The reservation data hold was not found.");
    public static readonly Error DataHoldProofUnavailable = new(
        "Reservations.DataHoldProofUnavailable",
        "The committed reservation data-hold result cannot be proven.");
    public static readonly Error AnonymisationRequestInvalid = new(
        "Reservations.AnonymisationRequestInvalid",
        "The reservation anonymisation request is invalid.");
    public static readonly Error AnonymisationIdempotencyConflict = new(
        "Reservations.AnonymisationIdempotencyConflict",
        "The anonymisation idempotency key is bound to a different request.");
    public static readonly Error AnonymisationProofUnavailable = new(
        "Reservations.AnonymisationProofUnavailable",
        "The committed reservation anonymisation result cannot be proven.");
    public static readonly Error AnonymisationRestoreRequestInvalid = new(
        "Reservations.AnonymisationRestoreRequestInvalid",
        "The reservation anonymisation restore request is invalid.");
    public static readonly Error AnonymisationRestoreProofConflict = new(
        "Reservations.AnonymisationRestoreProofConflict",
        "The restored reservation state conflicts with the protected anonymisation proof.");
    public static readonly Error RetentionExecutionNotFound = new(
        "Reservations.RetentionExecutionNotFound",
        "The reservation retention execution was not found.");
    public static readonly Error RetentionProofConflict = new(
        "Reservations.RetentionProofConflict",
        "The reservation retention proof is missing or conflicts with owner state.");
    public static readonly Error RetentionMutationInvalid = new(
        "Reservations.RetentionMutationInvalid",
        "The reservation retention mutation request is invalid.");
    public static Error AnonymisationBlocked(
        ReservationAnonymisationBlockerCode blockerCode) => new(
        $"Reservations.AnonymisationBlocked.{blockerCode}",
        "The reservation cannot be anonymised under the current owner state and policy.");
    public static Error ReservationGuestLinkInvalid => ReservationsDomainErrors.ReservationGuestLinkInvalid;
    public static Error ReservationGuestRoleOccupied => ReservationsDomainErrors.ReservationGuestRoleOccupied;
}
