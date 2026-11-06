namespace BunkFy.TimeZones;

using NodaTime;
using NodaTime.TimeZones;

/// <summary>
/// Performs civil-calendar calculations against the pinned embedded TZDB
/// catalog, independently of the serving operating system's time-zone data.
/// </summary>
public static class TimeZoneCalendarMath
{
    public const int MaximumSkippedLocalTimeAdjustmentMinutes = 180;
    private const int MaximumSkippedLocalTimeAdjustmentSeconds =
        MaximumSkippedLocalTimeAdjustmentMinutes * 60;

    /// <summary>
    /// Resolves the conservative start boundary of the local calendar day
    /// immediately after <paramref name="localBusinessDate"/> in a primary
    /// TZDB zone. A repeated midnight selects its later occurrence; a short
    /// midnight gap selects the first valid instant after the gap. The
    /// calculation uses only the pinned embedded catalog. A day skipped in its
    /// entirety, an excessive gap, an alias, or an identifier outside TZDB
    /// fails closed.
    /// </summary>
    public static bool TryGetStartOfNextLocalDay(
        DateOnly localBusinessDate,
        string canonicalTimeZoneId,
        out DateTimeOffset startAtUtc)
    {
        startAtUtc = default;
        if (!TimeZoneCatalog.Default.TryResolve(
                canonicalTimeZoneId,
                out TimeZoneCatalogResolution? resolution) ||
            resolution.Kind != TimeZoneCatalogResolutionKind.Canonical)
        {
            return false;
        }

        try
        {
            LocalDate nextLocalDate = new(
                localBusinessDate.Year,
                localBusinessDate.Month,
                localBusinessDate.Day);
            nextLocalDate = nextLocalDate.PlusDays(1);
            DateTimeZone zone = DateTimeZoneProviders.Tzdb[
                resolution.CanonicalTimeZoneId];
            ZoneLocalMapping mapping = zone.MapLocal(
                nextLocalDate.AtMidnight());
            Instant start;
            if (mapping.Count == 0)
            {
                int skippedSeconds =
                    mapping.LateInterval.WallOffset.Seconds -
                    mapping.EarlyInterval.WallOffset.Seconds;
                if (skippedSeconds is <= 0 or
                    > MaximumSkippedLocalTimeAdjustmentSeconds)
                {
                    return false;
                }

                start = mapping.LateInterval.Start;
            }
            else
            {
                // A repeated midnight is destructive-work evidence only once
                // both occurrences have elapsed.
                start = mapping.Last().ToInstant();
            }

            startAtUtc = start.ToDateTimeOffset();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or
            OverflowException or
            DateTimeZoneNotFoundException or
            SkippedTimeException)
        {
            startAtUtc = default;
            return false;
        }
    }

    /// <summary>
    /// Adds a Gregorian calendar period in a primary TZDB zone. Ambiguous local
    /// times resolve to their latest instant; skipped local times resolve to the
    /// first valid instant when the gap is no longer than three hours.
    /// </summary>
    public static bool TryAddCalendarPeriod(
        DateTimeOffset receivedAtUtc,
        string canonicalTimeZoneId,
        int years,
        int months,
        int days,
        out DateTimeOffset dueAtUtc)
    {
        dueAtUtc = default;
        if (receivedAtUtc == default ||
            years < 0 ||
            months < 0 ||
            days < 0 ||
            !TimeZoneCatalog.Default.TryResolve(
                canonicalTimeZoneId,
                out TimeZoneCatalogResolution? resolution) ||
            resolution.Kind != TimeZoneCatalogResolutionKind.Canonical)
        {
            return false;
        }

        try
        {
            DateTimeZone zone = DateTimeZoneProviders.Tzdb[
                resolution.CanonicalTimeZoneId];
            Instant received = Instant.FromDateTimeOffset(receivedAtUtc);
            LocalDateTime receivedLocal = received.InZone(zone).LocalDateTime;
            LocalDateTime dueLocal = receivedLocal
                .PlusYears(years)
                .PlusMonths(months)
                .PlusDays(days);
            ZoneLocalMapping mapping = zone.MapLocal(dueLocal);
            Instant due;
            if (mapping.Count == 0)
            {
                int skippedSeconds =
                    mapping.LateInterval.WallOffset.Seconds -
                    mapping.EarlyInterval.WallOffset.Seconds;
                if (skippedSeconds is <= 0 or
                    > MaximumSkippedLocalTimeAdjustmentSeconds)
                {
                    return false;
                }

                due = mapping.LateInterval.Start;
            }
            else
            {
                due = mapping.Last().ToInstant();
            }

            dueAtUtc = due.ToDateTimeOffset();
            return dueAtUtc > receivedAtUtc;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            OverflowException or
            InvalidOperationException)
        {
            dueAtUtc = default;
            return false;
        }
    }
}
