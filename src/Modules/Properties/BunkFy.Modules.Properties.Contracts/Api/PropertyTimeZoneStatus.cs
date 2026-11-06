namespace BunkFy.Modules.Properties.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(PropertyTimeZoneStatusJsonConverter))]
public enum PropertyTimeZoneStatus
{
    Unknown = 0,
    Canonical = 1,
    Alias = 2,
    Legacy = 3,
    Unrecognized = 4,
    RuntimeUnavailable = 5
}

public static class PropertyTimeZoneStatusNames
{
    public static string ToWireName(PropertyTimeZoneStatus status) =>
        status switch
        {
            PropertyTimeZoneStatus.Canonical => "canonical",
            PropertyTimeZoneStatus.Alias => "alias",
            PropertyTimeZoneStatus.Legacy => "legacy",
            PropertyTimeZoneStatus.Unrecognized => "unrecognized",
            PropertyTimeZoneStatus.RuntimeUnavailable => "runtime-unavailable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Property time-zone status is invalid.")
        };

    public static bool TryParse(
        string? value,
        out PropertyTimeZoneStatus status)
    {
        status = (value ?? string.Empty).Trim() switch
        {
            "canonical" => PropertyTimeZoneStatus.Canonical,
            "alias" => PropertyTimeZoneStatus.Alias,
            "legacy" => PropertyTimeZoneStatus.Legacy,
            "unrecognized" => PropertyTimeZoneStatus.Unrecognized,
            "runtime-unavailable" =>
                PropertyTimeZoneStatus.RuntimeUnavailable,
            _ => PropertyTimeZoneStatus.Unknown
        };
        return status is not PropertyTimeZoneStatus.Unknown;
    }
}
