namespace BunkFy.Modules.Properties.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class PropertyTimeZoneStatusJsonConverter
    : JsonConverter<PropertyTimeZoneStatus>
{
    public override PropertyTimeZoneStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException(
                "Property time-zone status must be a string.");
        }

        return PropertyTimeZoneStatusNames.TryParse(
                reader.GetString(),
                out PropertyTimeZoneStatus status)
            ? status
            : throw new JsonException(
                "Property time-zone status is invalid.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        PropertyTimeZoneStatus value,
        JsonSerializerOptions options)
    {
        try
        {
            writer.WriteStringValue(
                PropertyTimeZoneStatusNames.ToWireName(value));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                "Property time-zone status is invalid.",
                exception);
        }
    }
}
