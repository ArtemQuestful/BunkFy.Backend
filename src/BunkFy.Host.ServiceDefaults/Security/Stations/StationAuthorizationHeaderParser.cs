namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

internal enum StationAuthorizationHeaderKind
{
    Missing,
    NonStation,
    Station,
    Malformed
}

internal sealed record StationAuthorizationHeader(
    StationAuthorizationHeaderKind Kind,
    string? Token,
    string Category,
    int FieldValueCount,
    IReadOnlyList<int> Lengths);

internal static class StationAuthorizationHeaderParser
{
    public static StationAuthorizationHeader Parse(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue(HeaderNames.Authorization, out StringValues values) || values.Count == 0)
        {
            return new(
                StationAuthorizationHeaderKind.Missing,
                Token: null,
                "station_header_missing",
                FieldValueCount: 0,
                Lengths: []);
        }

        int[] lengths = values.Select(static value => value?.Length ?? 0).ToArray();
        if (values.Count != 1 || values[0] is not string value)
        {
            return Malformed("station_header_multiple", values.Count, lengths);
        }

        if (value.Length == 0 || value.Contains(',') || value.Any(static character => char.IsControl(character)))
        {
            return Malformed("station_header_malformed", values.Count, lengths);
        }

        int separator = value.IndexOf(' ');
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf(' ', separator + 1) >= 0 ||
            !IsHttpScheme(value.AsSpan(0, separator)))
        {
            return Malformed("station_header_malformed", values.Count, lengths);
        }

        ReadOnlySpan<char> scheme = value.AsSpan(0, separator);
        ReadOnlySpan<char> credential = value.AsSpan(separator + 1);
        if (!scheme.Equals(StationAuthenticationDefaults.WireScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsInvalidNonStationCredentialCharacter(credential))
            {
                return Malformed("station_header_malformed", values.Count, lengths);
            }

            return new(
                StationAuthorizationHeaderKind.NonStation,
                Token: null,
                "station_header_other_scheme",
                values.Count,
                lengths);
        }

        if (credential.Length is < 1 or > StationAuthenticationDefaults.MaximumTokenLength)
        {
            return Malformed("station_token_length_invalid", values.Count, lengths);
        }

        if (!IsCompactJws(credential))
        {
            return Malformed("station_token_characters_invalid", values.Count, lengths);
        }

        return new(
            StationAuthorizationHeaderKind.Station,
            credential.ToString(),
            "station_header_recognized",
            values.Count,
            lengths);
    }

    private static StationAuthorizationHeader Malformed(
        string category,
        int fieldValueCount,
        IReadOnlyList<int> lengths) =>
        new(StationAuthorizationHeaderKind.Malformed, Token: null, category, fieldValueCount, lengths);

    private static bool IsCompactJwsCharacter(char character) =>
        character is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '-' or '_' or '.';

    private static bool IsCompactJws(ReadOnlySpan<char> credential)
    {
        foreach (char character in credential)
        {
            if (!IsCompactJwsCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsInvalidNonStationCredentialCharacter(ReadOnlySpan<char> credential)
    {
        foreach (char character in credential)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHttpScheme(ReadOnlySpan<char> scheme)
    {
        foreach (char character in scheme)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\''
                    or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return scheme.Length > 0;
    }
}
