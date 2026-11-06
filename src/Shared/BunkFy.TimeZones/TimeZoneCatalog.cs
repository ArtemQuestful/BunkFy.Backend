namespace BunkFy.TimeZones;

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
/// Provides the immutable, process-independent time-zone catalog embedded in
/// the pinned Noda Time package.
/// </summary>
public sealed class TimeZoneCatalog
{
    public const string UniversalTimeZoneId = "Etc/UTC";

    private readonly IDateTimeZoneProvider provider;
    private readonly IDictionary<string, string> canonicalIdMap;
    private readonly HashSet<string> windowsIds;
    private readonly ReadOnlyDictionary<string, TimeZoneCatalogMetadata> metadata;

    private TimeZoneCatalog(
        TzdbDateTimeZoneSource source,
        IDateTimeZoneProvider provider)
    {
        this.provider = provider;
        this.CatalogVersion = source.VersionId;
        this.TzdbVersion = source.TzdbVersion;
        this.canonicalIdMap = source.CanonicalIdMap;
        this.windowsIds = source.WindowsMapping.MapZones
            .Select(mapping => mapping.WindowsId)
            .ToHashSet(StringComparer.Ordinal);

        string[] canonicalIds = source.CanonicalIdMap.Values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identifier => identifier, StringComparer.Ordinal)
            .ToArray();
        this.CanonicalIds = Array.AsReadOnly(canonicalIds);
        this.Resolutions = Array.AsReadOnly(source.CanonicalIdMap
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TimeZoneCatalogResolution(
                pair.Key,
                pair.Value,
                string.Equals(
                    pair.Key,
                    pair.Value,
                    StringComparison.Ordinal)
                    ? TimeZoneCatalogResolutionKind.Canonical
                    : TimeZoneCatalogResolutionKind.Alias))
            .ToArray());
        this.metadata = BuildMetadata(source, canonicalIds);
    }

    /// <summary>Gets the catalog backed by the TZDB data embedded in Noda Time.</summary>
    public static TimeZoneCatalog Default { get; } = new(
        TzdbDateTimeZoneSource.Default,
        DateTimeZoneProviders.Tzdb);

    /// <summary>Gets the full deterministic source version.</summary>
    public string CatalogVersion { get; }

    /// <summary>Gets the embedded IANA TZDB release identifier.</summary>
    public string TzdbVersion { get; }

    /// <summary>Gets all primary TZDB identifiers in ordinal order.</summary>
    public IReadOnlyList<string> CanonicalIds { get; }

    /// <summary>
    /// Gets every embedded TZDB identifier and its primary identifier,
    /// ordered by requested identifier. Windows mappings are not included.
    /// </summary>
    public IReadOnlyList<TimeZoneCatalogResolution> Resolutions { get; }

    /// <summary>
    /// Resolves an exact TZDB identifier to its primary identifier. This method
    /// uses only the embedded catalog and never the host operating system.
    /// </summary>
    public bool TryResolve(
        string? timeZoneId,
        [NotNullWhen(true)] out TimeZoneCatalogResolution? resolution)
    {
        resolution = null;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        string requested = timeZoneId.Trim();
        if (!this.canonicalIdMap.TryGetValue(
                requested,
                out string? canonicalId))
        {
            return false;
        }

        resolution = new(
            requested,
            canonicalId,
            string.Equals(requested, canonicalId, StringComparison.Ordinal)
                ? TimeZoneCatalogResolutionKind.Canonical
                : TimeZoneCatalogResolutionKind.Alias);
        return true;
    }

    /// <summary>
    /// Reports whether an exact identifier is a Windows time-zone identifier
    /// known to the embedded TZDB mapping. No IANA mapping is exposed because
    /// one Windows identifier can have territory-dependent meanings.
    /// </summary>
    public bool IsKnownWindowsId(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId) &&
        this.windowsIds.Contains(timeZoneId.Trim());

    /// <summary>
    /// Describes a primary identifier at one explicitly supplied instant.
    /// </summary>
    public TimeZoneCatalogEntry Describe(
        string canonicalTimeZoneId,
        DateTimeOffset observedAtUtc)
    {
        if (!this.canonicalIdMap.TryGetValue(
                canonicalTimeZoneId,
                out string? canonicalId) ||
            !string.Equals(
                canonicalTimeZoneId,
                canonicalId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A primary TZDB time-zone identifier is required.",
                nameof(canonicalTimeZoneId));
        }

        Offset offset = this.provider[canonicalId]
            .GetUtcOffset(Instant.FromDateTimeOffset(observedAtUtc));
        TimeZoneCatalogMetadata zoneMetadata = this.metadata[canonicalId];
        return new(
            canonicalId,
            zoneMetadata.Countries,
            zoneMetadata.Comment,
            offset.Seconds / 60);
    }

    private static ReadOnlyDictionary<string, TimeZoneCatalogMetadata>
        BuildMetadata(
            TzdbDateTimeZoneSource source,
            IEnumerable<string> canonicalIds)
    {
        Dictionary<string, TimeZoneCatalogMetadataBuilder> builders =
            canonicalIds.ToDictionary(
                identifier => identifier,
                _ => new TimeZoneCatalogMetadataBuilder(),
                StringComparer.Ordinal);

        foreach (TzdbZone1970Location location in
                 source.Zone1970Locations ?? [])
        {
            string canonicalId = source.CanonicalIdMap[location.ZoneId];
            TimeZoneCatalogMetadataBuilder builder = builders[canonicalId];
            foreach (TzdbZone1970Location.Country country in
                     location.Countries)
            {
                builder.AddCountry(country.Code, country.Name);
            }

            builder.AddComment(location.Comment);
        }

        foreach (TzdbZoneLocation location in source.ZoneLocations ?? [])
        {
            string canonicalId = source.CanonicalIdMap[location.ZoneId];
            TimeZoneCatalogMetadataBuilder builder = builders[canonicalId];
            builder.AddCountry(location.CountryCode, location.CountryName);
            builder.AddComment(location.Comment);
        }

        return new ReadOnlyDictionary<string, TimeZoneCatalogMetadata>(
            builders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Build(),
                StringComparer.Ordinal));
    }

    private sealed class TimeZoneCatalogMetadataBuilder
    {
        private readonly Dictionary<string, string> countries =
            new(StringComparer.Ordinal);
        private readonly SortedSet<string> comments =
            new(StringComparer.Ordinal);

        public void AddCountry(string code, string name) =>
            this.countries.TryAdd(code, name);

        public void AddComment(string? comment)
        {
            if (!string.IsNullOrWhiteSpace(comment))
            {
                this.comments.Add(comment.Trim());
            }
        }

        public TimeZoneCatalogMetadata Build()
        {
            TimeZoneCatalogCountry[] frozenCountries = this.countries
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TimeZoneCatalogCountry(
                    pair.Key,
                    pair.Value))
                .ToArray();
            return new(
                Array.AsReadOnly(frozenCountries),
                this.comments.Count == 0
                    ? null
                    : string.Join("; ", this.comments));
        }
    }

    private sealed record TimeZoneCatalogMetadata(
        IReadOnlyCollection<TimeZoneCatalogCountry> Countries,
        string? Comment);
}

public enum TimeZoneCatalogResolutionKind
{
    Unknown = 0,
    Canonical = 1,
    Alias = 2
}

public sealed record TimeZoneCatalogResolution(
    string RequestedTimeZoneId,
    string CanonicalTimeZoneId,
    TimeZoneCatalogResolutionKind Kind);

public sealed record TimeZoneCatalogEntry(
    string TimeZoneId,
    IReadOnlyCollection<TimeZoneCatalogCountry> Countries,
    string? Comment,
    int UtcOffsetMinutes);

public sealed record TimeZoneCatalogCountry(string Code, string Name);
