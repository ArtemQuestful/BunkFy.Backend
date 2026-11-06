namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ListPropertyTimeZoneComplianceQueryHandler(
    IPropertyTimeZoneComplianceReader reader,
    ISystemClock clock,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : IQueryHandler<ListPropertyTimeZoneComplianceQuery,
        PropertyTimeZoneCompliancePageDto>
{
    public async Task<Result<PropertyTimeZoneCompliancePageDto>> HandleAsync(
        ListPropertyTimeZoneComplianceQuery query,
        CancellationToken cancellationToken)
    {
        if (query.PageSize is <= 0 or
            > PropertiesContractLimits.PropertyTimeZonePageSizeMax ||
            query.Cursor?.Length > 2048)
        {
            return Result.Failure<PropertyTimeZoneCompliancePageDto>(
                PropertiesApplicationErrors.TimeZoneQueryInvalid);
        }

        PropertyTimeZoneComplianceReadPage page;
        try
        {
            page = await reader.ReadPageAsync(
                query.Cursor,
                query.PageSize,
                TimeZoneCatalog.Default.CatalogVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException) when (query.Cursor is not null)
        {
            return Result.Failure<PropertyTimeZoneCompliancePageDto>(
                PropertiesApplicationErrors.TimeZoneQueryInvalid);
        }
        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(observedAtUtc))
        {
            return Result.Failure<PropertyTimeZoneCompliancePageDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        PropertyTimeZoneComplianceItemDto[] properties = page.Properties
            .Select(property => ToDto(
                property,
                observedAtUtc,
                runtimeTimeZones))
            .ToArray();
        return Result.Success(new PropertyTimeZoneCompliancePageDto(
            TimeZoneCatalog.Default.CatalogVersion,
            observedAtUtc,
            properties,
            page.NextCursor,
            page.HasMore));
    }

    private static PropertyTimeZoneComplianceItemDto ToDto(
        PropertyTimeZoneComplianceReadModel property,
        DateTimeOffset observedAtUtc,
        TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    {
        bool canCorrect = property.Status == PropertyStatus.Active;
        PropertyTimeZoneHealth health =
            PropertyTimeZoneHealthClassifier.Classify(
                property.TimeZoneId,
                canCorrect,
                observedAtUtc,
                runtimeTimeZones);
        return new(
            property.PropertyId,
            property.Name,
            property.Code,
            property.TimeZoneId,
            health.Status,
            health.CanonicalTimeZoneId,
            property.Status,
            property.ProcessingStatus,
            property.OperatingCountryCode,
            property.Version,
            health.CorrectionAllowed);
    }
}
