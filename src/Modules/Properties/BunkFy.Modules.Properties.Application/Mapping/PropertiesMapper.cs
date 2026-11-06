namespace BunkFy.Modules.Properties.Application.Mapping;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.TimeZones;

public static class PropertiesMapper
{
    public static PropertyDto ToDto(
        Property property,
        DateTimeOffset observedAtUtc,
        TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    {
        PropertiesObservationTime.ThrowIfInvalid(
            observedAtUtc,
            nameof(observedAtUtc));
        PropertyTimeZoneHealth timeZone =
            PropertyTimeZoneHealthClassifier.Classify(
                property.TimeZoneId.Value,
                property.Status == PropertyState.Active,
                observedAtUtc,
                runtimeTimeZones);
        return new(
            property.Id,
            property.Name.Value,
            property.Code.Value,
            property.TimeZoneId.Value,
            timeZone.Status,
            timeZone.CanonicalTimeZoneId,
            timeZone.CatalogVersion,
            observedAtUtc,
            timeZone.CorrectionAllowed,
            MapStatus(property.Status),
            MapProcessingStatus(property.ProcessingState),
            MapGovernanceBinding(property),
            property.Version,
            property.CreatedAtUtc,
            property.UpdatedAtUtc,
            property.RetiredAtUtc);
    }

    public static PropertyListResponse ToListResponse(
        PropertyReadPage page,
        DateTimeOffset observedAtUtc,
        TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    {
        PropertiesObservationTime.ThrowIfInvalid(
            observedAtUtc,
            nameof(observedAtUtc));
        PropertyListItemDto[] properties = page.Properties
            .Select(property => ToListItemDto(
                property,
                observedAtUtc,
                runtimeTimeZones))
            .ToArray();
        return new PropertyListResponse(
            properties,
            page.Page,
            page.PageSize,
            page.HasMore);
    }

    private static PropertyListItemDto ToListItemDto(
        PropertyListReadModel property,
        DateTimeOffset observedAtUtc,
        TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    {
        PropertyTimeZoneHealth timeZone =
            PropertyTimeZoneHealthClassifier.Classify(
                property.TimeZoneId,
                property.Status == PropertyState.Active,
                observedAtUtc,
                runtimeTimeZones);
        return new PropertyListItemDto(
            property.PropertyId,
            property.Name,
            property.Code,
            property.TimeZoneId,
            timeZone.Status,
            timeZone.CanonicalTimeZoneId,
            timeZone.CatalogVersion,
            observedAtUtc,
            timeZone.CorrectionAllowed,
            MapStatus(property.Status),
            MapProcessingStatus(property.ProcessingState),
            property.Version);
    }

    public static PropertyMutationReceiptDto ToReceipt(Property property) =>
        new(
            property.Id,
            MapStatus(property.Status),
            MapProcessingStatus(property.ProcessingState),
            property.Version);

    public static RoomDto ToDto(Room room) =>
        new(
            room.Id,
            room.PropertyId,
            room.Name.Value,
            room.BuildingLabel?.Value,
            room.FloorLabel?.Value,
            MapStatus(room.Status),
            room.Version,
            room.CreatedAtUtc,
            room.UpdatedAtUtc,
            room.RetiredAtUtc);

    public static RoomMutationReceiptDto ToReceipt(Room room) =>
        new(
            room.PropertyId,
            room.Id,
            MapStatus(room.Status),
            room.Version);

    public static BedMutationReceiptDto ToReceipt(Bed bed, long roomVersion) =>
        new(
            bed.PropertyId,
            bed.RoomId,
            bed.Id,
            MapStatus(bed.Status),
            bed.Version,
            roomVersion);

    public static BedBatchMutationReceiptDto ToBatchReceipt(Room room, int affectedBedCount) =>
        new(room.PropertyId, room.Id, affectedBedCount, room.Version);

    public static PropertyStatus MapStatus(PropertyState status) =>
        status switch
        {
            PropertyState.Active => PropertyStatus.Active,
            PropertyState.Retired => PropertyStatus.Retired,
            _ => PropertyStatus.Unknown
        };

    public static PropertyProcessingStatus MapProcessingStatus(PropertyProcessingState status) =>
        status switch
        {
            PropertyProcessingState.Unconfigured => PropertyProcessingStatus.Unconfigured,
            PropertyProcessingState.Enabled => PropertyProcessingStatus.Enabled,
            PropertyProcessingState.Suspended => PropertyProcessingStatus.Suspended,
            _ => PropertyProcessingStatus.Unknown
        };

    public static PropertyGovernancePolicyBindingDto? MapGovernanceBinding(Property property) =>
        property.GovernanceBinding is not { } binding
            ? null
            : new PropertyGovernancePolicyBindingDto(
                binding.OperatingCountryCode,
                binding.PolicyId,
                binding.PolicyVersion,
                binding.DataRegionId,
                binding.TransferProfileId,
                binding.RetentionPolicyId,
                binding.RetentionPolicyVersion,
                binding.ContentSha256,
                binding.PolicyEffectiveAtUtc,
                binding.PolicyExpiresAtUtc,
                binding.ActivatedAtUtc,
                property.GovernanceAcknowledgements.Select(acknowledgement =>
                    new PropertyGovernanceAcknowledgementDto(
                        acknowledgement.AcknowledgementId,
                        acknowledgement.AcknowledgementVersion)).ToArray());

    public static RoomStatus MapStatus(RoomState status) =>
        status switch
        {
            RoomState.Active => RoomStatus.Active,
            RoomState.Retired => RoomStatus.Retired,
            _ => RoomStatus.Unknown
        };

    public static BedStatus MapStatus(BedState status) =>
        status switch
        {
            BedState.Active => BedStatus.Active,
            BedState.Retired => BedStatus.Retired,
            _ => BedStatus.Unknown
        };
}
