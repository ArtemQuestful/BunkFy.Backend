namespace BunkFy.Modules.Properties.Application.Handlers;

using System.Globalization;
using BunkFy.Modules.Properties.Domain.ValueObjects;

internal static class PropertyDetailsUpdateFingerprint
{
    private const string OmittedTimeZoneMarker = "time-zone:omitted";
    private const string SuppliedTimeZoneMarker = "time-zone:supplied";

    public static string ComputeV3(
        Guid propertyId,
        long expectedVersion,
        PropertyDetails details,
        string? requestedTimeZoneId)
        => Compute(
            "bunkfy-properties-details-update/v3",
            propertyId,
            expectedVersion,
            details,
            requestedTimeZoneId is null
                ? OmittedTimeZoneMarker
                : SuppliedTimeZoneMarker,
            requestedTimeZoneId is null
                ? string.Empty
                : PropertyTimeZoneId.RestorePersisted(
                    requestedTimeZoneId).Value);

    public static string? ComputeV2(
        Guid propertyId,
        long expectedVersion,
        PropertyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Gma.Framework.Results.Result<PropertyTimeZoneId> canonical =
            PropertyTimeZoneId.Create(details.TimeZoneId.Value);
        return canonical.IsFailure
            ? null
            : Compute(
                "bunkfy-properties-details-update/v2",
                propertyId,
                expectedVersion,
                details,
                marker: null,
                canonical.Value.Value);
    }

    public static string ComputeV1(
        Guid propertyId,
        long expectedVersion,
        PropertyDetails details,
        string requestedTimeZoneId)
    {
        ArgumentNullException.ThrowIfNull(details);
        string normalizedRequestedTimeZoneId =
            PropertyTimeZoneId.RestorePersisted(
                requestedTimeZoneId).Value;
        return Compute(
            "bunkfy-properties-details-update/v1",
            propertyId,
            expectedVersion,
            details,
            marker: null,
            normalizedRequestedTimeZoneId);
    }

    private static string Compute(
        string version,
        Guid propertyId,
        long expectedVersion,
        PropertyDetails details,
        string? marker,
        string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(details);
        return marker is null
            ? PropertiesMutationFingerprint.Compute(
                version,
                propertyId.ToString("N"),
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                details.Name.Value,
                details.Code.Value,
                timeZoneId)
            : PropertiesMutationFingerprint.Compute(
                version,
                propertyId.ToString("N"),
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                details.Name.Value,
                details.Code.Value,
                marker,
                timeZoneId);
    }
}
