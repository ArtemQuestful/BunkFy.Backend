namespace BunkFy.Modules.Properties.Domain.Errors;

using Gma.Framework.Results;

public static class PropertiesDomainErrors
{
    public static readonly Error ActorIdInvalid = new(
        "Properties.ActorIdInvalid",
        "The actor id is not valid.");
    public static readonly Error BedRetirementRequiresInventory = new(
        "Properties.BedRetirementRequiresInventory",
        "Bed retirement must be requested through Inventory so active reservations and blocks remain safe.");
    public static readonly Error RoomRetirementRequiresInventory = new(
        "Properties.RoomRetirementRequiresInventory",
        "Room retirement must be requested through Inventory so active reservations and blocks remain safe.");
    public static readonly Error TenantRequired = new("Properties.TenantRequired", "A tenant id is required.");
    public static readonly Error TenantInvalid = new("Properties.TenantInvalid", "The tenant id is not valid.");
    public static readonly Error DomainEventIdRequired = new("BunkFy.Modules.Properties.DomainEventIdRequired", "A domain event id is required.");
    public static readonly Error PropertyIdRequired = new("Properties.PropertyIdRequired", "A property id is required.");
    public static readonly Error RoomIdRequired = new("Properties.RoomIdRequired", "A room id is required.");
    public static readonly Error BedIdRequired = new("Properties.BedIdRequired", "A bed id is required.");
    public static readonly Error BedBatchRequired = new("Properties.BedBatchRequired", "At least one bed is required.");
    public static readonly Error BedBatchInvalid = new(
        "Properties.BedBatchInvalid",
        "The bed batch contains invalid or duplicate identifiers.");
    public static readonly Error PropertyNameRequired = new("Properties.PropertyNameRequired", "A property name is required.");
    public static readonly Error PropertyNameTooLong = new("Properties.PropertyNameTooLong", "The property name is too long.");
    public static readonly Error PropertyCodeRequired = new("Properties.PropertyCodeRequired", "A property code is required.");
    public static readonly Error PropertyCodeTooLong = new("Properties.PropertyCodeTooLong", "The property code is too long.");
    public static readonly Error PropertyCodeInvalid = new("Properties.PropertyCodeInvalid", "The property code is not valid.");
    public static readonly Error PropertyCodeAlreadyExists = new("Properties.PropertyCodeAlreadyExists", "A property with the same code already exists.");
    public static readonly Error PropertyNotFound = new("Properties.PropertyNotFound", "The property was not found.");
    public static readonly Error PropertyStatusUnknown = new("Properties.PropertyStatusUnknown", "The property status is unknown.");
    public static readonly Error PropertyAlreadyRetired = new("Properties.PropertyAlreadyRetired", "The property is already retired.");
    public static readonly Error PropertyRetired = new("Properties.PropertyRetired", "The property is retired.");
    public static readonly Error PropertyProcessingNotEnabled = new(
        "Properties.PropertyProcessingNotEnabled",
        "Data processing is not enabled for this property.");
    public static readonly Error PolicyBindingInvalid = new(
        "Properties.PolicyBindingInvalid",
        "The property policy binding is invalid.");
    public static readonly Error PolicyAcknowledgementsInvalid = new(
        "Properties.PolicyAcknowledgementsInvalid",
        "The property policy acknowledgements are invalid.");
    public static readonly Error PropertyHasActiveRooms = new("Properties.PropertyHasActiveRooms", "Retire all rooms before retiring the property.");
    public static readonly Error VersionConflict = new("Properties.VersionConflict", "The topology changed. Reload it and retry with the current version.");
    public static readonly Error TimeZoneRequired = new("Properties.TimeZoneRequired", "A property time zone is required.");
    public static readonly Error TimeZoneTooLong = new("Properties.TimeZoneTooLong", "The property time zone is too long.");
    public static readonly Error TimeZoneInvalid = new("Properties.TimeZoneInvalid", "The property time zone is not valid.");
    public static readonly Error TimeZoneDedicatedOperationRequired = new(
        "Properties.TimeZoneDedicatedOperationRequired",
        "Use the dedicated property time-zone operation to change the time zone.");
    public static readonly Error RoomNameRequired = new("Properties.RoomNameRequired", "A room name is required.");
    public static readonly Error RoomNameTooLong = new("Properties.RoomNameTooLong", "The room name is too long.");
    public static readonly Error PhysicalLabelTooLong = new("Properties.PhysicalLabelTooLong", "The physical label is too long.");
    public static readonly Error RoomAlreadyExists = new("Properties.RoomAlreadyExists", "A room with the same name already exists in this property.");
    public static readonly Error RoomNotFound = new("Properties.RoomNotFound", "The room was not found.");
    public static readonly Error RoomStatusUnknown = new("Properties.RoomStatusUnknown", "The room status is unknown.");
    public static readonly Error RoomAlreadyRetired = new("Properties.RoomAlreadyRetired", "The room is already retired.");
    public static readonly Error RoomRetired = new("Properties.RoomRetired", "The room is retired.");
    public static readonly Error RoomHasActiveBeds = new("Properties.RoomHasActiveBeds", "The room has active beds; an explicit cascade is required.");
    public static readonly Error BedLabelRequired = new("Properties.BedLabelRequired", "A bed label is required.");
    public static readonly Error BedLabelTooLong = new("Properties.BedLabelTooLong", "The bed label is too long.");
    public static readonly Error BedAlreadyExists = new("Properties.BedAlreadyExists", "A bed with the same label already exists in this room.");
    public static readonly Error BedNotFound = new("Properties.BedNotFound", "The bed was not found.");
    public static readonly Error BedStatusUnknown = new("Properties.BedStatusUnknown", "The bed status is unknown.");
    public static readonly Error BedAlreadyRetired = new("Properties.BedAlreadyRetired", "The bed is already retired.");
}
