namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class PropertyTimeZoneComplianceReader(
    PropertiesDbContext dbContext,
    IScopeContext scopeContext)
    : IPropertyTimeZoneComplianceReader
{
    public async Task<PropertyTimeZoneComplianceReadPage> ReadPageAsync(
        string? cursor,
        int pageSize,
        string catalogVersion,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or
            > PropertiesContractLimits.PropertyTimeZonePageSizeMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page size must be between 1 and " +
                $"{PropertiesContractLimits.PropertyTimeZonePageSizeMax}.");
        }

        if (!scopeContext.IsEnabled ||
            !TenantIds.TryNormalize(scopeContext.ScopeId, out string? tenantId))
        {
            throw new InvalidOperationException(
                "A tenant scope is required to read property time-zone compliance.");
        }

        PropertyTimeZoneCompliancePosition? after =
            PropertyTimeZoneComplianceCursor.Parse(
                cursor,
                tenantId,
                catalogVersion);
        IQueryable<Domain.Aggregates.Property> query = dbContext.Properties
            .AsNoTracking()
            .Where(property => property.ScopeId == tenantId);
        if (after is not null)
        {
            query = query.Where(property =>
                property.ProjectionOrdinal > after.ProjectionOrdinal ||
                (property.ProjectionOrdinal == after.ProjectionOrdinal &&
                 property.Id.CompareTo(after.PropertyId) > 0));
        }

        ComplianceRow[] loaded = await query
            .OrderBy(property => property.ProjectionOrdinal)
            .ThenBy(property => property.Id)
            .Select(property => new ComplianceRow(
                property.Id,
                property.ProjectionOrdinal,
                property.Name.Value,
                property.Code.Value,
                property.TimeZoneId.Value,
                property.Status,
                property.ProcessingState,
                property.GovernanceBinding == null
                    ? null
                    : property.GovernanceBinding.OperatingCountryCode,
                property.Version))
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = loaded.Length > pageSize;
        ComplianceRow[] selected = loaded.Take(pageSize).ToArray();
        PropertyTimeZoneComplianceReadModel[] properties = selected
            .Select(row => new PropertyTimeZoneComplianceReadModel(
                row.PropertyId,
                row.Name,
                row.Code,
                row.TimeZoneId,
                PropertiesMapper.MapStatus(row.Status),
                PropertiesMapper.MapProcessingStatus(row.ProcessingStatus),
                row.OperatingCountryCode,
                row.Version))
            .ToArray();
        string? nextCursor = hasMore
            ? PropertyTimeZoneComplianceCursor.Create(
                tenantId,
                catalogVersion,
                selected[^1].ProjectionOrdinal,
                selected[^1].PropertyId)
            : null;
        return new(Array.AsReadOnly(properties), nextCursor, hasMore);
    }

    private sealed record ComplianceRow(
        Guid PropertyId,
        long ProjectionOrdinal,
        string Name,
        string Code,
        string TimeZoneId,
        Domain.Aggregates.PropertyState Status,
        Domain.Aggregates.PropertyProcessingState ProcessingStatus,
        string? OperatingCountryCode,
        long Version);
}
