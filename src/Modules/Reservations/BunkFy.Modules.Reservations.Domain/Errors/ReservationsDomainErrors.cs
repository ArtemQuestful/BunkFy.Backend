namespace BunkFy.Modules.Reservations.Domain.Errors;

using Gma.Framework.Results;

public static class ReservationsDomainErrors
{
    public static readonly Error ReservationIdRequired = new("Reservations.ReservationIdRequired", "Reservation id is required.");
    public static readonly Error PropertyIdRequired = new("Reservations.PropertyIdRequired", "Property id is required.");
    public static readonly Error AllocationRequestIdRequired = new("Reservations.AllocationRequestIdRequired", "Allocation request id is required.");
    public static readonly Error AllocationIdRequired = new("Reservations.AllocationIdRequired", "Allocation id is required.");
    public static readonly Error StayRangeInvalid = new("Reservations.StayRangeInvalid", "Arrival must be before departure.");
    public static readonly Error ExpectedStayTimeInvalid = new("Reservations.ExpectedStayTimeInvalid", "Expected arrival and departure times must use minute precision.");
    public static readonly Error RequestedUnitsInvalid = new("Reservations.RequestedUnitsInvalid", "Requested units must contain unique, non-empty ids within the supported limit.");
    public static readonly Error PrimaryGuestNameInvalid = new("Reservations.PrimaryGuestNameInvalid", "Primary guest name is required and is too long.");
    public static readonly Error EmailInvalid = new("Reservations.EmailInvalid", "Email is too long.");
    public static readonly Error PhoneInvalid = new("Reservations.PhoneInvalid", "Phone is too long.");
    public static readonly Error GuestCountInvalid = new("Reservations.GuestCountInvalid", "Guest count must be positive.");
    public static readonly Error SourceInvalid = new("Reservations.SourceInvalid", "Reservation source is invalid.");
    public static readonly Error NotesInvalid = new("Reservations.NotesInvalid", "Reservation notes are too long.");
    public static readonly Error VersionConflict = new("Reservations.VersionConflict", "The reservation has changed. Refresh and retry.");
    public static readonly Error AllocationCorrelationMismatch = new("Reservations.AllocationCorrelationMismatch", "The allocation result does not match the current reservation request.");
    public static readonly Error InvalidTransition = new("Reservations.InvalidTransition", "The reservation cannot perform this lifecycle transition.");
    public static readonly Error DetailsRevisionConflict = new("Reservations.DetailsRevisionConflict", "The editable reservation details have changed. Refresh and retry.");
    public static readonly Error DetailsChangeProvenanceInvalid = new("Reservations.DetailsChangeProvenanceInvalid", "Reservation details change provenance is invalid.");
    public static readonly Error DataRightsCorrectionNoChanges = new("Reservations.DataRightsCorrectionNoChanges", "The correction does not change the selected reservation details.");
    public static readonly Error DataRightsCorrectionReceiptIdentityInvalid = new("Reservations.DataRightsCorrectionReceiptIdentityInvalid", "The correction receipt identity is invalid.");
    public static readonly Error DataRightsCorrectionReceiptVersionInvalid = new("Reservations.DataRightsCorrectionReceiptVersionInvalid", "The correction receipt version binding is invalid.");
    public static readonly Error DataRightsCorrectionReceiptFieldsInvalid = new("Reservations.DataRightsCorrectionReceiptFieldsInvalid", "The correction receipt field set is invalid.");
    public static readonly Error ProcessingRestrictionIdentityInvalid = new("Reservations.ProcessingRestrictionIdentityInvalid", "The processing-restriction identity is invalid.");
    public static readonly Error ProcessingRestrictionApprovalInvalid = new("Reservations.ProcessingRestrictionApprovalInvalid", "The processing-restriction approval coordinate is invalid.");
    public static readonly Error ProcessingRestrictionTransitionInvalid = new("Reservations.ProcessingRestrictionTransitionInvalid", "The processing-restriction transition is invalid.");
    public static readonly Error ProcessingRestrictionVersionConflict = new("Reservations.ProcessingRestrictionVersionConflict", "The processing restriction has changed.");
    public static readonly Error ProcessingRestrictionAlreadyReleased = new("Reservations.ProcessingRestrictionAlreadyReleased", "The processing restriction is already released.");
    public static readonly Error ProcessingRestrictionProjectionIdentityInvalid = new("Reservations.ProcessingRestrictionProjectionIdentityInvalid", "The processing-restriction projection identity is invalid.");
    public static readonly Error ProcessingRestrictionProjectionContractUnsupported = new("Reservations.ProcessingRestrictionProjectionContractUnsupported", "The processing-restriction projection contract is unsupported.");
    public static readonly Error ProcessingRestrictionProjectionVersionConflict = new("Reservations.ProcessingRestrictionProjectionVersionConflict", "The processing-restriction projection has changed.");
    public static readonly Error ProcessingRestrictionProjectionTransitionInvalid = new("Reservations.ProcessingRestrictionProjectionTransitionInvalid", "The processing-restriction projection transition is invalid.");
    public static readonly Error ProcessingRestrictionProjectionStateInvalid = new("Reservations.ProcessingRestrictionProjectionStateInvalid", "The processing-restriction projection state is invalid.");
    public static readonly Error ProcessingRestrictionReceiptIdentityInvalid = new("Reservations.ProcessingRestrictionReceiptIdentityInvalid", "The processing-restriction receipt identity is invalid.");
    public static readonly Error ProcessingRestrictionReceiptVersionInvalid = new("Reservations.ProcessingRestrictionReceiptVersionInvalid", "The processing-restriction receipt version is invalid.");
    public static readonly Error ProcessingRestrictionReceiptTransitionInvalid = new("Reservations.ProcessingRestrictionReceiptTransitionInvalid", "The processing-restriction receipt transition is invalid.");
    public static readonly Error DataHoldIdentityInvalid = new("Reservations.DataHoldIdentityInvalid", "The reservation data-hold identity is invalid.");
    public static readonly Error DataHoldReasonCodeInvalid = new("Reservations.DataHoldReasonCodeInvalid", "The reservation data-hold reason code is invalid.");
    public static readonly Error DataHoldActorInvalid = new("Reservations.DataHoldActorInvalid", "The reservation data-hold actor is invalid.");
    public static readonly Error DataHoldLifecycleInvalid = new("Reservations.DataHoldLifecycleInvalid", "The reservation data-hold lifecycle is invalid.");
    public static readonly Error DataHoldVersionConflict = new("Reservations.DataHoldVersionConflict", "The reservation data hold has changed.");
    public static readonly Error DataHoldAlreadyReleased = new("Reservations.DataHoldAlreadyReleased", "The reservation data hold is already released.");
    public static readonly Error DataHoldReceiptIdentityInvalid = new("Reservations.DataHoldReceiptIdentityInvalid", "The reservation data-hold receipt identity is invalid.");
    public static readonly Error DataHoldReceiptVersionInvalid = new("Reservations.DataHoldReceiptVersionInvalid", "The reservation data-hold receipt version is invalid.");
    public static readonly Error DataHoldReceiptLifecycleInvalid = new("Reservations.DataHoldReceiptLifecycleInvalid", "The reservation data-hold receipt lifecycle is invalid.");
    public static readonly Error ReservationAlreadyAnonymised = new("Reservations.ReservationAlreadyAnonymised", "The reservation has already been anonymised.");
    public static readonly Error ReservationNotEligibleForAnonymisation = new("Reservations.ReservationNotEligibleForAnonymisation", "The reservation is not in a terminal state that permits anonymisation.");
    public static readonly Error ReservationAnonymisationProvenanceInvalid = new("Reservations.ReservationAnonymisationProvenanceInvalid", "The reservation anonymisation provenance is invalid.");
    public static readonly Error ReservationAnonymisationReceiptInvalid = new("Reservations.ReservationAnonymisationReceiptInvalid", "The reservation anonymisation receipt is invalid.");
    public static readonly Error ReservationAnonymisationTombstoneInvalid = new("Reservations.ReservationAnonymisationTombstoneInvalid", "The reservation anonymisation tombstone is invalid.");
    public static readonly Error ReservationAnonymisationRestoreReceiptInvalid = new("Reservations.ReservationAnonymisationRestoreReceiptInvalid", "The reservation anonymisation restore receipt is invalid.");
    public static readonly Error ReservationAnonymisationRestoreStateInvalid = new("Reservations.ReservationAnonymisationRestoreStateInvalid", "The restored reservation state cannot be reconciled with the protected anonymisation proof.");
    public static readonly Error RetentionExecutionCoordinateInvalid = new("Reservations.RetentionExecutionCoordinateInvalid", "The reservation retention execution coordinate is invalid.");
    public static readonly Error RetentionExecutionTransitionInvalid = new("Reservations.RetentionExecutionTransitionInvalid", "The reservation retention execution transition is invalid.");
    public static readonly Error RetentionExecutionResultInvalid = new("Reservations.RetentionExecutionResultInvalid", "The reservation retention execution result is invalid.");
    public static readonly Error RetentionCheckpointInvalid = new("Reservations.RetentionCheckpointInvalid", "The reservation retention checkpoint is invalid.");
    public static readonly Error RetentionCheckpointConflict = new("Reservations.RetentionCheckpointConflict", "The reservation retention checkpoint has changed.");
    public static readonly Error RetentionReceiptInvalid = new("Reservations.RetentionReceiptInvalid", "The reservation retention receipt is invalid.");
    public static readonly Error AllocationAmendmentInProgress = new("Reservations.AllocationAmendmentInProgress", "An allocation-affecting reservation amendment is already in progress.");
    public static readonly Error AllocationAmendmentInvalid = new("Reservations.AllocationAmendmentInvalid", "The allocation-affecting reservation amendment is invalid.");
    public static readonly Error StayAmendmentOperationIdentityInvalid = new("Reservations.StayAmendmentOperationIdentityInvalid", "The stay-amendment operation identity is invalid.");
    public static readonly Error StayAmendmentOperationRequestInvalid = new("Reservations.StayAmendmentOperationRequestInvalid", "The stay-amendment operation request is invalid.");
    public static readonly Error StayAmendmentOperationTransitionInvalid = new("Reservations.StayAmendmentOperationTransitionInvalid", "The stay-amendment operation cannot perform this transition.");
    public static readonly Error StayAmendmentOperationVersionConflict = new("Reservations.StayAmendmentOperationVersionConflict", "The stay-amendment operation has changed.");
    public static readonly Error StayAmendmentReconcileTooSoon = new("Reservations.StayAmendmentReconcileTooSoon", "The stay-amendment operation is not yet eligible for reconciliation.");
    public static readonly Error StayAmendmentOutcomeMismatch = new("Reservations.StayAmendmentOutcomeMismatch", "The allocation outcome does not match the stay-amendment operation.");
    public static readonly Error StayBusinessDateInvalid = new("Reservations.StayBusinessDateInvalid", "The business date is not valid for this stay transition.");
    public static readonly Error StayProvenanceInvalid = new("Reservations.StayProvenanceInvalid", "Stay lifecycle actor provenance is invalid.");
    public static readonly Error ReservationGuestLinkInvalid = new("Reservations.ReservationGuestLinkInvalid", "The reservation guest link is invalid.");
    public static readonly Error ReservationGuestRoleOccupied = new("Reservations.ReservationGuestRoleOccupied", "The reservation guest role is already occupied and replacement was not requested.");
    public static readonly Error GuestRecordLinkProcessIdentityInvalid = new(
        "Reservations.GuestRecordLinkProcessIdentityInvalid",
        "The Reservation Guest Record link process identity is invalid.");
    public static readonly Error GuestRecordLinkProcessActorInvalid = new(
        "Reservations.GuestRecordLinkProcessActorInvalid",
        "The Reservation Guest Record link process actor is invalid.");
    public static readonly Error GuestRecordLinkProcessLifecycleInvalid = new(
        "Reservations.GuestRecordLinkProcessLifecycleInvalid",
        "The Reservation Guest Record link process lifecycle is invalid.");
    public static readonly Error GuestRecordLinkProcessCorrelationMismatch = new(
        "Reservations.GuestRecordLinkProcessCorrelationMismatch",
        "The Guest creation confirmation does not match the Reservation link process.");
    public static readonly Error GuestRecordLinkProcessTransitionInvalid = new(
        "Reservations.GuestRecordLinkProcessTransitionInvalid",
        "The Reservation Guest Record link process cannot perform this transition.");
    public static readonly Error GuestRecordLinkProcessReviewReasonInvalid = new(
        "Reservations.GuestRecordLinkProcessReviewReasonInvalid",
        "The Reservation Guest Record link review reason is invalid.");
}
