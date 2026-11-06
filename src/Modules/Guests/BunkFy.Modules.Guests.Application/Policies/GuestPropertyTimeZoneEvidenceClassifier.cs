namespace BunkFy.Modules.Guests.Application.Policies;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;

/// <summary>
/// Classifies consumer-derived property time-zone evidence against the
/// immutable TZDB catalog embedded in the application.
/// </summary>
public static class GuestPropertyTimeZoneEvidenceClassifier
{
    public static GuestPropertyTimeZoneWriteModel Classify(
        string scopeId,
        Guid propertyId,
        string timeZoneId,
        GuestPropertyTimeZoneEvidenceSource evidenceSource,
        long sourceVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty property identifier is required.",
                nameof(propertyId));
        }

        if (evidenceSource is not
            (GuestPropertyTimeZoneEvidenceSource.Generic or
             GuestPropertyTimeZoneEvidenceSource.Rebuild))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceSource),
                evidenceSource,
                "Only consumer-derived Generic or Rebuild evidence can be classified.");
        }

        if (sourceVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceVersion),
                sourceVersion,
                "The evidence source version must be positive.");
        }

        TimeZoneCatalog catalog = TimeZoneCatalog.Default;
        string? canonicalTimeZoneId = null;
        string? catalogVersion = null;
        PropertyTimeZoneStatus status;
        if (catalog.TryResolve(
                timeZoneId,
                out TimeZoneCatalogResolution? resolution))
        {
            canonicalTimeZoneId = resolution.CanonicalTimeZoneId;
            catalogVersion = catalog.CatalogVersion;
            status = resolution.Kind ==
                TimeZoneCatalogResolutionKind.Canonical
                    ? PropertyTimeZoneStatus.Canonical
                    : PropertyTimeZoneStatus.Alias;
        }
        else
        {
            status = catalog.IsKnownWindowsId(timeZoneId)
                ? PropertyTimeZoneStatus.Legacy
                : PropertyTimeZoneStatus.Unrecognized;
        }

        return new(
            scopeId,
            propertyId,
            timeZoneId,
            canonicalTimeZoneId,
            status,
            catalogVersion,
            evidenceSource,
            sourceVersion);
    }
}
