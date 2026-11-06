namespace BunkFy.Modules.Properties.Application.Handlers;

using System.Text;
using System.Text.Json;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ListPropertyTimeZoneCatalogQueryHandler
    : IQueryHandler<ListPropertyTimeZoneCatalogQuery,
        PropertyTimeZoneCatalogPageDto>
{
    private const int MaximumSearchLength = 128;
    private const int MaximumCursorLength = 2048;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly ISystemClock clock;
    private readonly Func<string, DateTimeOffset, bool>
        runtimeCompatibility;

    public ListPropertyTimeZoneCatalogQueryHandler(
        ISystemClock clock,
        TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
        : this(
            clock,
            runtimeTimeZones.IsCompatible)
    {
    }

    internal ListPropertyTimeZoneCatalogQueryHandler(
        ISystemClock clock,
        Func<string, DateTimeOffset, bool> runtimeCompatibility)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.runtimeCompatibility = runtimeCompatibility ??
            throw new ArgumentNullException(nameof(runtimeCompatibility));
    }

    public Task<Result<PropertyTimeZoneCatalogPageDto>> HandleAsync(
        ListPropertyTimeZoneCatalogQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalize(
                query,
                out string? search,
                out string? countryCode))
        {
            return Invalid();
        }

        TimeZoneCatalog catalog = TimeZoneCatalog.Default;
        CatalogCursor? cursor = null;
        if (query.Cursor is not null &&
            (!TryDecodeCursor(query.Cursor, out cursor) ||
             cursor is null ||
             !string.Equals(
                 cursor.CatalogVersion,
                 catalog.CatalogVersion,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 cursor.Search,
                 search,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 cursor.CountryCode,
                 countryCode,
                 StringComparison.Ordinal)))
        {
            return Invalid();
        }

        DateTimeOffset observedAtUtc = this.clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(observedAtUtc))
        {
            return Task.FromResult(Result.Failure<
                PropertyTimeZoneCatalogPageDto>(
                    PropertiesApplicationErrors.TimeSourceUnavailable));
        }

        TimeZoneCatalogEntry[] matching = catalog.CanonicalIds
            .Select(identifier => catalog.Describe(
                identifier,
                observedAtUtc))
            .Where(entry => Matches(entry, search, countryCode))
            .ToArray();

        int startIndex = 0;
        if (cursor is not null)
        {
            int lastIndex = Array.FindIndex(
                matching,
                entry => string.Equals(
                    entry.TimeZoneId,
                    cursor.LastTimeZoneId,
                    StringComparison.Ordinal));
            if (lastIndex < 0)
            {
                return Invalid();
            }

            startIndex = lastIndex + 1;
        }

        TimeZoneCatalogEntry[] pageEntries = matching
            .Skip(startIndex)
            .Take(query.PageSize)
            .ToArray();
        PropertyTimeZoneCatalogItemDto[] page = pageEntries
            .Select(entry => this.ToDto(entry, observedAtUtc))
            .ToArray();
        bool hasMore = startIndex + pageEntries.Length < matching.Length;
        string? nextCursor = hasMore && page.Length > 0
            ? EncodeCursor(new(
                catalog.CatalogVersion,
                search,
                countryCode,
                page[^1].TimeZoneId))
            : null;

        return Task.FromResult(Result.Success(
            new PropertyTimeZoneCatalogPageDto(
                catalog.CatalogVersion,
                observedAtUtc,
                page,
                nextCursor,
                hasMore)));
    }

    private static bool TryNormalize(
        ListPropertyTimeZoneCatalogQuery query,
        out string? search,
        out string? countryCode)
    {
        search = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : query.Search.Trim().ToUpperInvariant();
        countryCode = string.IsNullOrWhiteSpace(query.CountryCode)
            ? null
            : query.CountryCode.Trim().ToUpperInvariant();
        return query.PageSize is > 0 and <=
               PropertiesContractLimits.PropertyTimeZonePageSizeMax &&
               (query.Cursor is null ||
                query.Cursor.Length is > 0 and <= MaximumCursorLength) &&
               (search is null ||
                (search.Length <= MaximumSearchLength &&
                 !search.Any(char.IsControl))) &&
               (countryCode is null ||
                (countryCode.Length == 2 &&
                 countryCode.All(character =>
                    character is >= 'A' and <= 'Z')));
    }

    private static bool Matches(
        TimeZoneCatalogEntry entry,
        string? search,
        string? countryCode) =>
        (countryCode is null || entry.Countries.Any(country =>
            string.Equals(
                country.Code,
                countryCode,
                StringComparison.Ordinal))) &&
        (search is null ||
         entry.TimeZoneId.Contains(
             search,
             StringComparison.OrdinalIgnoreCase) ||
         entry.Comment?.Contains(
             search,
             StringComparison.OrdinalIgnoreCase) == true ||
         entry.Countries.Any(country =>
             country.Code.Contains(
                 search,
                 StringComparison.OrdinalIgnoreCase) ||
             country.Name.Contains(
                 search,
                 StringComparison.OrdinalIgnoreCase)));

    private PropertyTimeZoneCatalogItemDto ToDto(
        TimeZoneCatalogEntry entry,
        DateTimeOffset observedAtUtc) => new(
            entry.TimeZoneId,
            entry.Countries.Select(country =>
                new PropertyTimeZoneCountryDto(
                    country.Code,
                    country.Name)).ToArray(),
            entry.Comment,
            entry.UtcOffsetMinutes,
            this.runtimeCompatibility(
                entry.TimeZoneId,
                observedAtUtc));

    private static string EncodeCursor(CatalogCursor cursor)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            cursor,
            SerializerOptions);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(
        string value,
        out CatalogCursor? cursor)
    {
        cursor = null;
        if (value.Length is 0 or > MaximumCursorLength ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            cursor = JsonSerializer.Deserialize<CatalogCursor>(
                Convert.FromBase64String(padded),
                SerializerOptions);
            return cursor is not null &&
                   !string.IsNullOrWhiteSpace(cursor.CatalogVersion) &&
                   !string.IsNullOrWhiteSpace(cursor.LastTimeZoneId) &&
                   string.Equals(
                       EncodeCursor(cursor),
                       value,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private static Task<Result<PropertyTimeZoneCatalogPageDto>> Invalid() =>
        Task.FromResult(Result.Failure<PropertyTimeZoneCatalogPageDto>(
            PropertiesApplicationErrors.TimeZoneQueryInvalid));

    private sealed record CatalogCursor(
        string CatalogVersion,
        string? Search,
        string? CountryCode,
        string LastTimeZoneId);
}
