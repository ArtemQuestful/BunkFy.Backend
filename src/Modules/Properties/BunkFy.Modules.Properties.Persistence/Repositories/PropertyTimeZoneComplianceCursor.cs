namespace BunkFy.Modules.Properties.Persistence.Repositories;

using System.Security.Cryptography;
using System.Text;

internal static class PropertyTimeZoneComplianceCursor
{
    private const string Domain =
        "bunkfy-properties-time-zone-compliance-cursor/v1";
    private const string Prefix = "ptzc1_";
    private const int DigestLength = 32;

    public static string Create(
        string tenantId,
        string catalogVersion,
        long projectionOrdinal,
        Guid propertyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);
        if (projectionOrdinal <= 0 || propertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "A compliance cursor requires a positive projection ordinal and property id.",
                nameof(propertyId));
        }

        string payload = $"{tenantId.Length}:{tenantId}" +
            $"{catalogVersion.Length}:{catalogVersion}" +
            projectionOrdinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            $":{propertyId:D}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Domain}|{payload}")));
        return Prefix + Convert.ToBase64String(payloadBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_') + "." + digest;
    }

    public static PropertyTimeZoneCompliancePosition? Parse(
        string? cursor,
        string tenantId,
        string catalogVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string normalized = cursor.Trim();
        if (normalized.Length > 512 ||
            normalized.Any(char.IsControl) ||
            !normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw InvalidCursor();
        }

        string encoded = normalized[Prefix.Length..];
        int digestSeparator = encoded.LastIndexOf('.');
        if (digestSeparator <= 0 ||
            encoded.Length - digestSeparator - 1 != DigestLength * 2)
        {
            throw InvalidCursor();
        }

        string suppliedDigest = encoded[(digestSeparator + 1)..];
        string encodedPayload = encoded[..digestSeparator];
        if (encodedPayload.Length == 0 ||
            encodedPayload.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_') ||
            suppliedDigest.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw InvalidCursor();
        }

        byte[] payloadBytes;
        try
        {
            string base64 = encodedPayload
                .Replace('-', '+')
                .Replace('_', '/');
            base64 = base64.PadRight(
                base64.Length + ((4 - (base64.Length % 4)) % 4),
                '=');
            payloadBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }

        string payload;
        try
        {
            payload = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(payloadBytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidCursor();
        }

        string expectedDigest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Domain}|{payload}")));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(suppliedDigest),
                Encoding.ASCII.GetBytes(expectedDigest)))
        {
            throw InvalidCursor();
        }

        int separator = payload.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 ||
            !int.TryParse(
                payload.AsSpan(0, separator),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int tenantLength) ||
            tenantLength != tenantId.Length ||
            payload.Length < separator + 1 + tenantLength + 1)
        {
            throw InvalidCursor();
        }

        ReadOnlySpan<char> encodedTenant =
            payload.AsSpan(separator + 1, tenantLength);
        ReadOnlySpan<char> catalogAndPosition =
            payload.AsSpan(separator + 1 + tenantLength);
        int catalogSeparator = catalogAndPosition.IndexOf(':');
        if (catalogSeparator <= 0 ||
            !int.TryParse(
                catalogAndPosition[..catalogSeparator],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int catalogLength) ||
            catalogLength != catalogVersion.Length ||
            catalogAndPosition.Length < catalogSeparator + 1 + catalogLength)
        {
            throw InvalidCursor();
        }

        ReadOnlySpan<char> encodedCatalog = catalogAndPosition.Slice(
            catalogSeparator + 1,
            catalogLength);
        ReadOnlySpan<char> position = catalogAndPosition[
            (catalogSeparator + 1 + catalogLength)..];
        int positionSeparator = position.IndexOf(':');
        if (!encodedTenant.SequenceEqual(tenantId) ||
            !encodedCatalog.SequenceEqual(catalogVersion) ||
            positionSeparator <= 0 ||
            position.Length - positionSeparator - 1 != 36 ||
            !long.TryParse(
                position[..positionSeparator],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long projectionOrdinal) ||
            projectionOrdinal <= 0 ||
            !Guid.TryParseExact(
                position[(positionSeparator + 1)..],
                "D",
                out Guid propertyId) ||
            propertyId == Guid.Empty)
        {
            throw InvalidCursor();
        }

        return new(projectionOrdinal, propertyId);
    }

    private static ArgumentException InvalidCursor() =>
        new("The property time-zone compliance cursor is invalid.", "cursor");
}

internal sealed record PropertyTimeZoneCompliancePosition(
    long ProjectionOrdinal,
    Guid PropertyId);
