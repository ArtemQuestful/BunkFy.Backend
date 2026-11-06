namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ListPropertiesQueryHandler(
    IPropertiesReadRepository repository,
    ISystemClock clock,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : IQueryHandler<ListPropertiesQuery, PropertyListResponse>
{
    public async Task<Result<PropertyListResponse>> HandleAsync(
        ListPropertiesQuery query,
        CancellationToken cancellationToken)
    {
        PageRequest pageRequest = PageRequest.Normalize(query.Page, query.PageSize);
        PropertyReadPage page = await repository
            .ListPropertiesAsync(pageRequest, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(observedAtUtc))
        {
            return Result.Failure<PropertyListResponse>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        return Result.Success(PropertiesMapper.ToListResponse(
            page,
            observedAtUtc,
            runtimeTimeZones));
    }
}
