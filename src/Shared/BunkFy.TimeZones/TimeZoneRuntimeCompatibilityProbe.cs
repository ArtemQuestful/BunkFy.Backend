namespace BunkFy.TimeZones;

using System.Collections.Concurrent;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
/// Verifies that the serving runtime implements the same future UTC offsets as
/// the embedded TZDB catalog over the bounded Properties operating horizon.
/// </summary>
public sealed class TimeZoneRuntimeCompatibilityProbe
{
    public const int OperationalHorizonYears = 5;

    private readonly Func<string, TimeZoneInfo?> runtimeZoneResolver;
    private readonly ConcurrentDictionary<CompatibilityKey, bool> cache = new();

    public TimeZoneRuntimeCompatibilityProbe()
        : this(ResolveSystemZone)
    { }

    internal TimeZoneRuntimeCompatibilityProbe(
        Func<string, TimeZoneInfo?> runtimeZoneResolver) =>
        this.runtimeZoneResolver = runtimeZoneResolver ??
            throw new ArgumentNullException(
                nameof(runtimeZoneResolver));

    internal static TimeZoneRuntimeCompatibilityProbe CreateForTesting(
        Func<string, TimeZoneInfo?> resolver) =>
        new(resolver);

    public static TimeZoneRuntimeCompatibilityProbe Default { get; } = new();

    /// <summary>
    /// Returns true only when the identifier is a primary embedded TZDB ID and
    /// the host agrees with embedded TZDB offsets from the observation instant
    /// through at least five years. Results are cached per UTC observation year.
    /// </summary>
    public bool IsCompatible(
        string canonicalTimeZoneId,
        DateTimeOffset observedAtUtc)
    {
        if (!TimeZoneCatalog.Default.TryResolve(
                canonicalTimeZoneId,
                out TimeZoneCatalogResolution? resolution) ||
            resolution.Kind != TimeZoneCatalogResolutionKind.Canonical ||
            observedAtUtc == default ||
            observedAtUtc.Offset != TimeSpan.Zero ||
            observedAtUtc.Year is < 1900 or > 9993)
        {
            return false;
        }

        return this.cache.GetOrAdd(
            new(canonicalTimeZoneId, observedAtUtc.Year),
            this.Evaluate);
    }

    private bool Evaluate(CompatibilityKey key)
    {
        TimeZoneInfo? runtimeZoneInfo;
        try
        {
            runtimeZoneInfo = this.runtimeZoneResolver(
                key.CanonicalTimeZoneId);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return false;
        }

        if (runtimeZoneInfo is null)
        {
            return false;
        }

        DateTimeOffset start = new(
            key.ObservationYear,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        // Starting at January 1 and checking six complete years guarantees at
        // least five years beyond every instant in the observation year.
        DateTimeOffset end = start.AddYears(
            OperationalHorizonYears + 1);
        Instant startInstant = Instant.FromDateTimeOffset(start);
        Instant endInstant = Instant.FromDateTimeOffset(end);

        try
        {
            DateTimeZone embedded = DateTimeZoneProviders.Tzdb[
                key.CanonicalTimeZoneId];
            DateTimeZone runtime = BclDateTimeZone.FromTimeZoneInfo(
                runtimeZoneInfo);
            foreach (Instant sample in ComparisonInstants(
                         embedded,
                         runtime,
                         startInstant,
                         endInstant))
            {
                if (embedded.GetUtcOffset(sample) !=
                    runtime.GetUtcOffset(sample))
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    private static SortedSet<Instant> ComparisonInstants(
        DateTimeZone embedded,
        DateTimeZone runtime,
        Instant start,
        Instant end)
    {
        SortedSet<Instant> boundaries = [start, end];
        AddBoundaries(embedded, start, end, boundaries);
        AddBoundaries(runtime, start, end, boundaries);

        Instant[] ordered = boundaries.ToArray();
        SortedSet<Instant> samples = [.. boundaries];
        for (int index = 0; index < ordered.Length - 1; index++)
        {
            Duration span = ordered[index + 1] - ordered[index];
            if (span > Duration.Zero)
            {
                samples.Add(ordered[index] + (span / 2));
            }
        }

        return samples;
    }

    private static void AddBoundaries(
        DateTimeZone zone,
        Instant start,
        Instant end,
        SortedSet<Instant> boundaries)
    {
        foreach (ZoneInterval interval in zone.GetZoneIntervals(start, end))
        {
            if (interval.HasStart &&
                interval.Start > start &&
                interval.Start < end)
            {
                boundaries.Add(interval.Start);
            }
        }
    }

    private static TimeZoneInfo? ResolveSystemZone(
        string canonicalTimeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                canonicalTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private readonly record struct CompatibilityKey(
        string CanonicalTimeZoneId,
        int ObservationYear);
}
