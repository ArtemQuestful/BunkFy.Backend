namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Permissions;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ListVisiblePropertiesQueryHandler(
    IPropertiesReadRepository repository,
    IAccessGrantScopeReader grantScopeReader,
    IScopeContext scopeContext,
    ISystemClock clock,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse>
{
    public async Task<Result<PropertyListResponse>> HandleAsync(
        ListVisiblePropertiesQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<PropertyListResponse>(PropertiesApplicationErrors.TenantRequired);
        }

        AccessScope tenantScope = AccessScope.Create(
            AccessScopeSegment.Create("tenant", scopeContext.ScopeId));
        IReadOnlyList<AccessGrantScope> grants = await grantScopeReader
            .ListGrantedScopesAsync(
                query.Subject,
                PermissionCode.Create(PropertiesAdminPermissionCodes.Read),
                cancellationToken)
            .ConfigureAwait(false);

        PropertiesVisibilityScope visibility = grants.Any(grant => grant.Grants(tenantScope))
            ? PropertiesVisibilityScope.All
            : PropertiesVisibilityScope.Restricted(GetGrantedPropertyIds(grants, scopeContext.ScopeId));

        if (!visibility.IncludesAllProperties && visibility.PropertyIds.Count == 0)
        {
            return Result.Failure<PropertyListResponse>(PropertiesApplicationErrors.AccessDenied);
        }

        PageRequest pageRequest = PageRequest.Normalize(query.Page, query.PageSize);
        PropertyReadPage page = await repository
            .ListVisiblePropertiesAsync(pageRequest, visibility, cancellationToken)
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

    private static IEnumerable<Guid> GetGrantedPropertyIds(
        IEnumerable<AccessGrantScope> grants,
        string scopeId)
    {
        foreach (AccessGrantScope grant in grants)
        {
            IReadOnlyList<AccessScopeSegment> segments = grant.Scope.Segments;
            if (segments.Count != 2 ||
                !string.Equals(segments[0].Name, "tenant", StringComparison.Ordinal) ||
                !string.Equals(segments[0].Value, scopeId, StringComparison.Ordinal) ||
                !string.Equals(segments[1].Name, "property", StringComparison.Ordinal) ||
                !Guid.TryParse(segments[1].Value, out Guid propertyId))
            {
                continue;
            }

            yield return propertyId;
        }
    }
}
