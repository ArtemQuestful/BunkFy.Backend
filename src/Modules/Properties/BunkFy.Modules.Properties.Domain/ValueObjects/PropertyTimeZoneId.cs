namespace BunkFy.Modules.Properties.Domain.ValueObjects;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.TimeZones;
using Gma.Framework.Results;

public readonly record struct PropertyTimeZoneId
{
    private readonly string? value;

    private PropertyTimeZoneId(string value) => this.value = value;

    public string Value => this.value ?? string.Empty;

    internal bool IsPrimaryCanonical =>
        TimeZoneCatalog.Default.TryResolve(
            this.Value,
            out TimeZoneCatalogResolution? resolution) &&
        resolution.Kind == TimeZoneCatalogResolutionKind.Canonical;

    public static Result<PropertyTimeZoneId> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<PropertyTimeZoneId>(PropertiesDomainErrors.TimeZoneRequired);
        }

        string normalized = value.Trim();
        if (normalized.Length > Property.TimeZoneIdMaxLength)
        {
            return Result.Failure<PropertyTimeZoneId>(PropertiesDomainErrors.TimeZoneTooLong);
        }

        if (normalized.Any(char.IsControl) ||
            !TimeZoneCatalog.Default.TryResolve(
                normalized,
                out TimeZoneCatalogResolution? resolution))
        {
            return Result.Failure<PropertyTimeZoneId>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }

        return Result.Success(new PropertyTimeZoneId(
            resolution.CanonicalTimeZoneId));
    }

    public static PropertyTimeZoneId RestorePersisted(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A persisted property time zone is required.",
                nameof(value));
        }

        string normalized = value.Trim();
        if (normalized.Length > Property.TimeZoneIdMaxLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The persisted property time zone is structurally invalid.",
                nameof(value));
        }

        return new PropertyTimeZoneId(normalized);
    }

    public override string ToString() => this.Value;
}
