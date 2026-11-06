namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GetPropertyTimeZoneRecoveryQueryHandler(
    IPropertyTimeZoneRevisionReader revisions,
    IPropertiesReadRepository properties,
    ISystemClock clock,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : IQueryHandler<GetPropertyTimeZoneRecoveryQuery,
        PropertyTimeZoneRecoveryDto>
{
    public async Task<Result<PropertyTimeZoneRecoveryDto>> HandleAsync(
        GetPropertyTimeZoneRecoveryQuery query,
        CancellationToken cancellationToken)
    {
        PropertyTimeZoneRevisionReadModel? revision =
            await revisions.GetAsync(
                query.PropertyId,
                query.OperationId,
                cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return Result.Failure<PropertyTimeZoneRecoveryDto>(
                PropertiesApplicationErrors.TimeZoneOperationNotFound);
        }

        Property? property = await properties.GetPropertyAsync(
            query.PropertyId,
            cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<PropertyTimeZoneRecoveryDto>(
                PropertiesDomainErrors.PropertyNotFound);
        }

        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(observedAtUtc))
        {
            return Result.Failure<PropertyTimeZoneRecoveryDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        PropertyDto current = PropertiesMapper.ToDto(
            property,
            observedAtUtc,
            runtimeTimeZones);

        return Result.Success(new PropertyTimeZoneRecoveryDto(
            SetPropertyTimeZoneCommandHandler.ToReceipt(revision),
            current.TimeZoneId,
            current.TimeZoneStatus,
            current.CanonicalTimeZoneId,
            current.TimeZoneObservedAtUtc,
            current.Version));
    }
}
