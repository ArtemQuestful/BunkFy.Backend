namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GetPropertyQueryHandler(
    IPropertiesReadRepository repository,
    ISystemClock clock,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : IQueryHandler<GetPropertyQuery, PropertyDto>
{
    public async Task<Result<PropertyDto>> HandleAsync(GetPropertyQuery query, CancellationToken cancellationToken)
    {
        Property? property = await repository.GetPropertyAsync(query.PropertyId, cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<PropertyDto>(PropertiesDomainErrors.PropertyNotFound);
        }

        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(observedAtUtc))
        {
            return Result.Failure<PropertyDto>(PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        return Result.Success(PropertiesMapper.ToDto(
            property,
            observedAtUtc,
            runtimeTimeZones));
    }
}
