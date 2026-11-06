namespace BunkFy.Modules.Guests.Domain.Errors;

using Gma.Framework.Results;

public static class GuestsDomainErrors
{
    public static readonly Error GuestIdRequired = new("Guests.GuestIdRequired", "A guest id is required.");
    public static readonly Error PropertyIdRequired = new("Guests.PropertyIdRequired", "A property id is required.");
    public static readonly Error TenantInvalid = new("Guests.TenantInvalid", "The tenant id is invalid.");
    public static readonly Error DisplayNameInvalid = new("Guests.DisplayNameInvalid", "The display name is invalid.");
    public static readonly Error LegalNameInvalid = new("Guests.LegalNameInvalid", "The legal name is invalid.");
    public static readonly Error EmailInvalid = new("Guests.EmailInvalid", "The email address is invalid.");
    public static readonly Error PhoneInvalid = new("Guests.PhoneInvalid", "The phone number is invalid.");
    public static readonly Error DateOfBirthInvalid = new("Guests.DateOfBirthInvalid", "The date of birth is invalid.");
    public static readonly Error NationalityInvalid = new("Guests.NationalityInvalid", "The nationality country code is invalid.");
    public static readonly Error LanguageTagInvalid = new("Guests.LanguageTagInvalid", "The preferred language tag is invalid.");
    public static readonly Error LanguageTagsInvalid = new(
        "Guests.LanguageTagsInvalid", "Provide at most 12 nonempty language tags of up to 35 supported characters each.");
    public static readonly Error LanguageTagsContradictPreferred = new(
        "Guests.LanguageTagsContradictPreferred", "The preferred language must be included in an explicitly supplied language collection.");
    public static readonly Error NotesInvalid = new("Guests.NotesInvalid", "The notes are invalid.");
    public static readonly Error ActorInvalid = new("Guests.ActorInvalid", "The actor id is invalid.");
    public static readonly Error EventIdRequired = new("Guests.EventIdRequired", "A domain event id is required.");
    public static readonly Error CreationConfirmationInvalid = new(
        "Guests.CreationConfirmationInvalid",
        "The Guest creation confirmation coordinate is invalid.");
    public static readonly Error VersionConflict = new("Guests.VersionConflict", "The guest profile version has changed.");
    public static readonly Error GuestArchived = new("Guests.GuestArchived", "The guest profile is archived.");
    public static readonly Error GuestAlreadyArchived = new("Guests.GuestAlreadyArchived", "The guest profile is already archived.");
    public static readonly Error GuestStatusUnknown = new("Guests.GuestStatusUnknown", "The guest profile status is unknown.");
    public static readonly Error GuestAlreadyAnonymised = new(
        "Guests.GuestAlreadyAnonymised",
        "The guest profile is already anonymised.");
    public static readonly Error GuestNotActiveForAnonymisation = new(
        "Guests.GuestNotActiveForAnonymisation",
        "Only an active guest profile can be anonymised.");
    public static readonly Error AnonymisationTimestampInvalid = new(
        "Guests.AnonymisationTimestampInvalid",
        "The anonymisation timestamp is invalid.");
    public static readonly Error AnonymisationReceiptInvalid = new(
        "Guests.AnonymisationReceiptInvalid",
        "The Guest anonymisation receipt is invalid.");
    public static readonly Error AnonymisationTombstoneInvalid = new(
        "Guests.AnonymisationTombstoneInvalid",
        "The Guest anonymisation tombstone is invalid.");
    public static readonly Error AnonymisationRestoreTransitionInvalid = new(
        "Guests.AnonymisationRestoreTransitionInvalid",
        "The Guest profile cannot be restored to anonymised state.");
    public static readonly Error AnonymisationRestoreReceiptInvalid = new(
        "Guests.AnonymisationRestoreReceiptInvalid",
        "The Guest anonymisation restore receipt is invalid.");
    public static readonly Error RetentionExecutionCoordinateInvalid = new(
        "Guests.RetentionExecutionCoordinateInvalid",
        "The Guest retention execution coordinate is invalid.");
    public static readonly Error RetentionExecutionTransitionInvalid = new(
        "Guests.RetentionExecutionTransitionInvalid",
        "The Guest retention execution transition is invalid.");
    public static readonly Error RetentionExecutionResultInvalid = new(
        "Guests.RetentionExecutionResultInvalid",
        "The Guest retention execution result is invalid.");
    public static readonly Error RetentionCheckpointInvalid = new(
        "Guests.RetentionCheckpointInvalid",
        "The Guest retention sweep checkpoint is invalid.");
    public static readonly Error RetentionCheckpointConflict = new(
        "Guests.RetentionCheckpointConflict",
        "The Guest retention sweep checkpoint has changed.");
    public static readonly Error RetentionReceiptInvalid = new(
        "Guests.RetentionReceiptInvalid",
        "The Guest retention receipt is invalid.");
    public static readonly Error GuestNotRetainable = new(
        "Guests.GuestNotRetainable",
        "The Guest profile cannot be anonymised by retention.");
    public static readonly Error CorrectionReceiptIdentityInvalid = new(
        "Guests.CorrectionReceiptIdentityInvalid",
        "The correction receipt identity is invalid.");
    public static readonly Error CorrectionReceiptVersionInvalid = new(
        "Guests.CorrectionReceiptVersionInvalid",
        "The correction receipt version is invalid.");
    public static readonly Error CorrectionReceiptFieldsInvalid = new(
        "Guests.CorrectionReceiptFieldsInvalid",
        "The correction receipt field set is invalid.");
    public static readonly Error RestrictionProjectionIdentityInvalid = new(
        "Guests.RestrictionProjectionIdentityInvalid",
        "The processing-restriction projection identity is invalid.");
    public static readonly Error RestrictionProjectionContractUnsupported = new(
        "Guests.RestrictionProjectionContractUnsupported",
        "The processing-restriction projection contract is unsupported.");
    public static readonly Error RestrictionProjectionVersionConflict = new(
        "Guests.RestrictionProjectionVersionConflict",
        "The processing-restriction projection version has changed.");
    public static readonly Error RestrictionProjectionTransitionInvalid = new(
        "Guests.RestrictionProjectionTransitionInvalid",
        "The processing-restriction transition is invalid.");
    public static readonly Error RestrictionProjectionStateInvalid = new(
        "Guests.RestrictionProjectionStateInvalid",
        "The processing-restriction projection state is invalid.");
    public static readonly Error RestrictionIdentityInvalid = new(
        "Guests.RestrictionIdentityInvalid",
        "The processing-restriction identity is invalid.");
    public static readonly Error RestrictionApprovalInvalid = new(
        "Guests.RestrictionApprovalInvalid",
        "The processing-restriction approval coordinate is invalid.");
    public static readonly Error RestrictionTransitionInvalid = new(
        "Guests.RestrictionTransitionInvalid",
        "The processing-restriction transition is invalid.");
    public static readonly Error RestrictionVersionConflict = new(
        "Guests.RestrictionVersionConflict",
        "The processing-restriction version has changed.");
    public static readonly Error RestrictionAlreadyReleased = new(
        "Guests.RestrictionAlreadyReleased",
        "The processing restriction is already released.");
    public static readonly Error RestrictionReceiptIdentityInvalid = new(
        "Guests.RestrictionReceiptIdentityInvalid",
        "The processing-restriction receipt identity is invalid.");
    public static readonly Error RestrictionReceiptVersionInvalid = new(
        "Guests.RestrictionReceiptVersionInvalid",
        "The processing-restriction receipt version is invalid.");
    public static readonly Error RestrictionReceiptTransitionInvalid = new(
        "Guests.RestrictionReceiptTransitionInvalid",
        "The processing-restriction receipt transition is invalid.");
    public static readonly Error DataHoldIdentityInvalid = new(
        "Guests.DataHoldIdentityInvalid",
        "The Guest data-hold identity is invalid.");
    public static readonly Error DataHoldReasonCodeInvalid = new(
        "Guests.DataHoldReasonCodeInvalid",
        "The Guest data-hold reason code is invalid.");
    public static readonly Error DataHoldLifecycleInvalid = new(
        "Guests.DataHoldLifecycleInvalid",
        "The Guest data-hold lifecycle is invalid.");
    public static readonly Error DataHoldVersionConflict = new(
        "Guests.DataHoldVersionConflict",
        "The Guest data-hold version has changed.");
    public static readonly Error DataHoldAlreadyReleased = new(
        "Guests.DataHoldAlreadyReleased",
        "The Guest data hold is already released.");
    public static readonly Error DataHoldReceiptIdentityInvalid = new(
        "Guests.DataHoldReceiptIdentityInvalid",
        "The Guest data-hold receipt identity is invalid.");
    public static readonly Error DataHoldReceiptTransitionInvalid = new(
        "Guests.DataHoldReceiptTransitionInvalid",
        "The Guest data-hold receipt transition is invalid.");
}
