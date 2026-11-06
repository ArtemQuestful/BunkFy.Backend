namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;

public static class PropertyTimeZoneHealthClassifier
{
    public static PropertyTimeZoneHealth Classify(
        string timeZoneId,
        bool correctionAllowed,
        DateTimeOffset observedAtUtc,
        TimeZoneRuntimeCompatibilityProbe runtimeCompatibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        ArgumentNullException.ThrowIfNull(runtimeCompatibility);
        PropertiesObservationTime.ThrowIfInvalid(
            observedAtUtc,
            nameof(observedAtUtc));
        TimeZoneCatalog catalog = TimeZoneCatalog.Default;
        if (catalog.TryResolve(
                timeZoneId,
                out TimeZoneCatalogResolution? resolution))
        {
            bool runtimeReady = runtimeCompatibility.IsCompatible(
                resolution.CanonicalTimeZoneId,
                observedAtUtc);
            PropertyTimeZoneStatus status = runtimeReady
                ? resolution.Kind == TimeZoneCatalogResolutionKind.Canonical
                    ? PropertyTimeZoneStatus.Canonical
                    : PropertyTimeZoneStatus.Alias
                : PropertyTimeZoneStatus.RuntimeUnavailable;
            return new(
                status,
                resolution.CanonicalTimeZoneId,
                catalog.CatalogVersion,
                correctionAllowed &&
                status is PropertyTimeZoneStatus.Alias or
                    PropertyTimeZoneStatus.RuntimeUnavailable);
        }

        return new(
            catalog.IsKnownWindowsId(timeZoneId)
                ? PropertyTimeZoneStatus.Legacy
                : PropertyTimeZoneStatus.Unrecognized,
            null,
            catalog.CatalogVersion,
            correctionAllowed);
    }

}

public sealed record PropertyTimeZoneHealth(
    PropertyTimeZoneStatus Status,
    string? CanonicalTimeZoneId,
    string CatalogVersion,
    bool CorrectionAllowed);
