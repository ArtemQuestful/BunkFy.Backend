namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.TimeZones;

internal static class PinnedTzdbCountryPolicyTimeZoneRules
{
    public static CountryPolicyTimeZoneRules Instance { get; } = new(
        TryResolve,
        TimeZoneCalendarMath.TryAddCalendarPeriod);

    private static bool TryResolve(
        string? timeZoneId,
        out CountryPolicyTimeZoneResolution resolution)
    {
        if (!TimeZoneCatalog.Default.TryResolve(
                timeZoneId,
                out TimeZoneCatalogResolution? catalogResolution))
        {
            resolution = default;
            return false;
        }

        resolution = new(
            catalogResolution.RequestedTimeZoneId,
            catalogResolution.CanonicalTimeZoneId,
            catalogResolution.Kind == TimeZoneCatalogResolutionKind.Canonical
                ? CountryPolicyTimeZoneResolutionKind.Canonical
                : CountryPolicyTimeZoneResolutionKind.Alias);
        return true;
    }
}
