namespace BunkFy.Modules.Properties.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class PropertyTimeZoneChangeKindJsonConverter
    : JsonConverter<PropertyTimeZoneChangeKind>
{
    public override PropertyTimeZoneChangeKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException(
                "Property time-zone change kind must be a string.");
        }

        return PropertyTimeZoneChangeKindNames.TryParse(
                reader.GetString(),
                out PropertyTimeZoneChangeKind kind)
            ? kind
            : throw new JsonException(
                "Property time-zone change kind is invalid.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        PropertyTimeZoneChangeKind value,
        JsonSerializerOptions options)
    {
        try
        {
            writer.WriteStringValue(
                PropertyTimeZoneChangeKindNames.ToWireName(value));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                "Property time-zone change kind is invalid.",
                exception);
        }
    }
}
