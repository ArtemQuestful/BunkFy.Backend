namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.Properties.Application.Ports;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class PropertyTimeZoneRevisionRepository(
    PropertiesDbContext dbContext,
    IScopeContext scopeContext)
    : IPropertyTimeZoneRevisionWriter,
      IPropertyTimeZoneRevisionReader
{
    public Task AppendAsync(
        PropertyTimeZoneRevisionWriteModel revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        string tenantId = this.GetTenantId();
        if (!TenantIds.TryNormalize(
                revision.ScopeId,
                out string? revisionTenantId) ||
            !string.Equals(
                tenantId,
                revisionTenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A property time-zone revision must match the current tenant scope.");
        }

        dbContext.PropertyTimeZoneOperations.Add(
            new PropertyTimeZoneOperation(revision));
        return Task.CompletedTask;
    }

    public async Task<PropertyTimeZoneRevisionReadModel?> GetAsync(
        Guid propertyId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        string tenantId = this.GetTenantId();
        PropertyTimeZoneOperation? operation = await dbContext
            .PropertyTimeZoneOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ScopeId == tenantId &&
                    item.PropertyId == propertyId &&
                    item.OperationId == operationId,
                cancellationToken)
            .ConfigureAwait(false);
        return operation?.ToReadModel();
    }

    private string GetTenantId()
    {
        if (!scopeContext.IsEnabled ||
            !TenantIds.TryNormalize(scopeContext.ScopeId, out string? tenantId))
        {
            throw new InvalidOperationException(
                "A tenant scope is required for property time-zone revisions.");
        }

        return tenantId;
    }
}
