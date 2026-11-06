namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;

internal sealed class PropertyStationEligibilitySource(PropertiesDbContext dbContext)
    : IPropertyStationEligibilitySource
{
    public async Task<PropertyStationEligibilitySnapshot?> FindAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dbContext.ScopeFilterEnabled || propertyId == Guid.Empty ||
            !TenantIds.TryNormalize(scopeId, out string? tenantId) ||
            !string.Equals(tenantId, dbContext.CurrentScopeId, StringComparison.Ordinal))
        {
            return null;
        }

        var row = await dbContext.Properties.AsNoTracking()
            .Where(property => property.ScopeId == tenantId && property.Id == propertyId)
            .Select(property => new
            {
                property.ScopeId,
                property.Id,
                property.Status,
                property.Version,
                property.Name,
                property.ProcessingState,
                TimeZoneId = property.TimeZoneId.Value
            }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null || row.Version < 1 ? null : new(row.ScopeId, row.Id, row.Status switch
        {
            PropertyState.Active => PropertyStatus.Active,
            PropertyState.Retired => PropertyStatus.Retired,
            _ => PropertyStatus.Unknown
        }, row.Version, row.ProcessingState switch
        {
            PropertyProcessingState.Unconfigured => PropertyProcessingStatus.Unconfigured,
            PropertyProcessingState.Enabled => PropertyProcessingStatus.Enabled,
            PropertyProcessingState.Suspended => PropertyProcessingStatus.Suspended,
            _ => PropertyProcessingStatus.Unknown
        }, row.TimeZoneId, row.Name.Value);
    }
}
