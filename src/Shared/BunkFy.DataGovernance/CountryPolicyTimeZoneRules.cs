namespace BunkFy.DataGovernance;

public delegate bool CountryPolicyTimeZoneResolver(
    string? timeZoneId,
    out CountryPolicyTimeZoneResolution resolution);

public delegate bool CountryPolicyCalendarPeriodAdder(
    DateTimeOffset receivedAtUtc,
    string canonicalTimeZoneId,
    int years,
    int months,
    int days,
    out DateTimeOffset dueAtUtc);

/// <summary>
/// Supplies deterministic time-zone resolution and civil-calendar semantics
/// to the dependency-free country-policy engine.
/// </summary>
public sealed class CountryPolicyTimeZoneRules
{
    private readonly CountryPolicyTimeZoneResolver resolver;
    private readonly CountryPolicyCalendarPeriodAdder calendarPeriodAdder;

    public CountryPolicyTimeZoneRules(
        CountryPolicyTimeZoneResolver resolver,
        CountryPolicyCalendarPeriodAdder calendarPeriodAdder)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(calendarPeriodAdder);
        this.resolver = resolver;
        this.calendarPeriodAdder = calendarPeriodAdder;
    }

    /// <summary>
    /// Gets a fail-closed rule set for registries that intentionally contain
    /// no time-zone-bearing policy artifacts.
    /// </summary>
    public static CountryPolicyTimeZoneRules Unavailable { get; } = new(
        UnavailableResolver,
        UnavailableCalendarPeriodAdder);

    public bool TryResolve(
        string? timeZoneId,
        out CountryPolicyTimeZoneResolution resolution) =>
        this.resolver(timeZoneId, out resolution);

    public bool TryAddCalendarPeriod(
        DateTimeOffset receivedAtUtc,
        string canonicalTimeZoneId,
        int years,
        int months,
        int days,
        out DateTimeOffset dueAtUtc) =>
        this.calendarPeriodAdder(
            receivedAtUtc,
            canonicalTimeZoneId,
            years,
            months,
            days,
            out dueAtUtc);

    private static bool UnavailableResolver(
        string? timeZoneId,
        out CountryPolicyTimeZoneResolution resolution)
    {
        _ = timeZoneId;
        resolution = default;
        return false;
    }

    private static bool UnavailableCalendarPeriodAdder(
        DateTimeOffset receivedAtUtc,
        string canonicalTimeZoneId,
        int years,
        int months,
        int days,
        out DateTimeOffset dueAtUtc)
    {
        _ = receivedAtUtc;
        _ = canonicalTimeZoneId;
        _ = years;
        _ = months;
        _ = days;
        dueAtUtc = default;
        return false;
    }
}

public enum CountryPolicyTimeZoneResolutionKind
{
    Unknown = 0,
    Canonical = 1,
    Alias = 2
}

public readonly record struct CountryPolicyTimeZoneResolution(
    string RequestedTimeZoneId,
    string CanonicalTimeZoneId,
    CountryPolicyTimeZoneResolutionKind Kind);
