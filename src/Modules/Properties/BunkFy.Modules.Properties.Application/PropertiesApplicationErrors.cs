namespace BunkFy.Modules.Properties.Application;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
using Gma.Framework.Results;
using BunkFy.DataGovernance;

public static class PropertiesApplicationErrors
{
    public static readonly Error AccessDenied = new("Properties.AccessDenied", "The subject cannot access the requested property scope.");
    public static readonly Error ConfirmationRequired = new(
        "Properties.ConfirmationRequired",
        "Confirmation is required.");
    public static readonly Error CreationOperationInvalid = new(
        "Properties.CreationOperationInvalid",
        "A valid property creation operation id is required.");
    public static readonly Error CreationOperationConflict = new(
        "Properties.CreationOperationConflict",
        "The property creation operation was already used for different details.");
    public static readonly Error ManagementOperationInvalid = new(
        "Properties.ManagementOperationInvalid",
        "A valid property management operation id is required.");
    public static readonly Error ManagementOperationConflict = new(
        "Properties.ManagementOperationConflict",
        "The property management operation was already used for a different request.");
    public static readonly Error TimeZoneRuntimeUnavailable = new(
        "Properties.TimeZoneRuntimeUnavailable",
        "The selected primary IANA/TZDB time-zone identifier is valid, but this serving runtime cannot execute UTC-offset rules compatible with the pinned catalog over the operational horizon.");
    public static readonly Error TimeZoneDedicatedOperationRequired =
        PropertiesDomainErrors.TimeZoneDedicatedOperationRequired;
    public static readonly Error TimeZoneOperationNotFound = new(
        "Properties.TimeZoneOperationNotFound",
        "The property time-zone operation was not found.");
    public static readonly Error TimeZoneQueryInvalid = new(
        "Properties.TimeZoneQueryInvalid",
        "The property time-zone query is invalid.");
    public static readonly Error TimeSourceUnavailable = new(
        "Properties.TimeSourceUnavailable",
        "The serving runtime did not provide a supported UTC observation instant.");
    public static readonly Error TenantRequired = PropertiesDomainErrors.TenantRequired;
    public static readonly Error PropertyNotFound = PropertiesDomainErrors.PropertyNotFound;
    public static readonly Error PropertyCodeAlreadyExists = PropertiesDomainErrors.PropertyCodeAlreadyExists;
    public static readonly Error PropertyStatusUnknown = PropertiesDomainErrors.PropertyStatusUnknown;
    public static readonly Error PropertyAlreadyRetired = PropertiesDomainErrors.PropertyAlreadyRetired;
    public static readonly Error PropertyRetired = PropertiesDomainErrors.PropertyRetired;
    public static readonly Error PropertyProcessingNotEnabled = PropertiesDomainErrors.PropertyProcessingNotEnabled;
    public static readonly Error PropertyHasActiveRooms = PropertiesDomainErrors.PropertyHasActiveRooms;
    public static readonly Error VersionConflict = PropertiesDomainErrors.VersionConflict;
    public static readonly Error RoomAlreadyExists = PropertiesDomainErrors.RoomAlreadyExists;
    public static readonly Error RoomNotFound = PropertiesDomainErrors.RoomNotFound;
    public static readonly Error RoomStatusUnknown = PropertiesDomainErrors.RoomStatusUnknown;
    public static readonly Error RoomRetired = PropertiesDomainErrors.RoomRetired;
    public static readonly Error RoomHasActiveBeds = PropertiesDomainErrors.RoomHasActiveBeds;
    public static readonly Error BedAlreadyExists = PropertiesDomainErrors.BedAlreadyExists;
    public static readonly Error BedBatchRequired = PropertiesDomainErrors.BedBatchRequired;
    public static readonly Error BedBatchTooLarge = new(
        "Properties.BedBatchTooLarge",
        $"A bed batch cannot contain more than {PropertiesContractLimits.MaximumBedsPerBatch} beds.");
    public static readonly Error BedNotFound = PropertiesDomainErrors.BedNotFound;
    public static readonly Error BedStatusUnknown = PropertiesDomainErrors.BedStatusUnknown;
    public static readonly Error BedAlreadyRetired = PropertiesDomainErrors.BedAlreadyRetired;
    public static readonly Error BedRetirementRequiresInventory = PropertiesDomainErrors.BedRetirementRequiresInventory;
    public static readonly Error RoomRetirementRequiresInventory = PropertiesDomainErrors.RoomRetirementRequiresInventory;
    public static readonly Error ProcessingLifecycleRestricted = new(
        "Properties.ProcessingLifecycleRestricted",
        "The workspace lifecycle does not permit property activation.");
    public static readonly Error ProcessingLifecycleAdmissionUnavailable = new(
        "Properties.ProcessingLifecycleAdmissionUnavailable",
        "Workspace lifecycle admission is temporarily unavailable.");
    public static readonly Error WorkspaceProcessingRestricted = new(
        "Properties.WorkspaceProcessingRestricted",
        "The workspace is not accepting operational changes.");
    public static readonly Error WorkspaceProcessingAdmissionUnavailable = new(
        "Properties.WorkspaceProcessingAdmissionUnavailable",
        "Workspace processing admission is temporarily unavailable.");

    public static Error CountryPolicyDenied(CountryPolicyDecisionReason reason) =>
        new(
            $"Properties.CountryPolicy.{reason}",
            "The selected country policy does not permit this operation.");
    public static IReadOnlyList<Error> CountryPolicyDenials { get; } =
        Enum.GetValues<CountryPolicyDecisionReason>()
            .Where(reason => reason is not CountryPolicyDecisionReason.Unknown and not CountryPolicyDecisionReason.Allowed)
            .Select(CountryPolicyDenied)
            .ToArray();
}
