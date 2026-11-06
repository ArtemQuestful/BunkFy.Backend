namespace BunkFy.DataGovernance;

public static class CountryPolicyCalendarDeadlineCalculator
{
    public static bool TryCalculate(
        DateTimeOffset receivedAtUtc,
        string timeZoneId,
        CountryPolicyCalendarPeriod period,
        CountryPolicyTimeZoneRules timeZoneRules,
        out DateTimeOffset dueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(timeZoneRules);
        dueAtUtc = default;
        if (receivedAtUtc == default ||
            string.IsNullOrWhiteSpace(timeZoneId) ||
            !IsValid(period))
        {
            return false;
        }

        return timeZoneRules.TryAddCalendarPeriod(
            receivedAtUtc,
            timeZoneId,
            period.Years,
            period.Months,
            period.Days,
            out dueAtUtc);
    }

    internal static bool IsValid(CountryPolicyCalendarPeriod? period) =>
        period is not null &&
        period.Years is >= 0 and <= 10 &&
        period.Months is >= 0 and <= 11 &&
        period.Days is >= 0 and <= 366 &&
        (period.Years > 0 || period.Months > 0 || period.Days > 0);
}
