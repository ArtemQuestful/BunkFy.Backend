namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffPropertyProjectionRepository(StaffDbContext dbContext)
    : IStaffPropertyProjectionRepository
{
    private const string PropertyProjectionLockPrefix =
        "bunkfy:staff:property-projection:";

    public Task<bool> IsActiveAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        string scopeId = this.RequireCurrentScope();
        if (propertyId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return dbContext.PropertyProjections.AsNoTracking().AnyAsync(
            property => property.ScopeId == scopeId &&
                property.Id == propertyId &&
                property.Status == PropertyStatus.Active,
            cancellationToken);
    }

    public async Task<bool> AreAllActiveAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        string scopeId = this.RequireCurrentScope();
        Guid[] distinctIds = propertyIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return true;
        }

        if (distinctIds.Any(propertyId => propertyId == Guid.Empty))
        {
            return false;
        }

        int activeCount = await dbContext.PropertyProjections.AsNoTracking()
            .CountAsync(
                property => property.ScopeId == scopeId &&
                    distinctIds.Contains(property.Id) &&
                    property.Status == PropertyStatus.Active,
                cancellationToken)
            .ConfigureAwait(false);
        return activeCount == distinctIds.Length;
    }

    public async Task ApplyAsync(StaffPropertyProjectionWriteModel property,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        string scopeId = this.RequireCurrentScope(
            property.ScopeId,
            property.PropertyId);
        await this.AcquirePropertyProjectionLockAsync(
            scopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        StaffPropertyProjection? current = dbContext.PropertyProjections.Local
            .FirstOrDefault(item =>
                item.ScopeId == scopeId && item.Id == property.PropertyId) ??
            await dbContext.PropertyProjections.FirstOrDefaultAsync(
                item => item.ScopeId == scopeId && item.Id == property.PropertyId,
                cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            dbContext.PropertyProjections.Add(new StaffPropertyProjection(scopeId,
                property.PropertyId, property.Name, property.Status, property.Version));
            return;
        }

        current.Apply(property.Name, property.Status, property.Version);
    }

    private string RequireCurrentScope()
    {
        if (!dbContext.ScopeFilterEnabled ||
            !TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? canonicalScopeId) ||
            !string.Equals(
                canonicalScopeId,
                dbContext.CurrentScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Staff property projection requires an active canonical scope.");
        }

        return canonicalScopeId;
    }

    private string RequireCurrentScope(string tenantId, Guid propertyId)
    {
        string currentScopeId = this.RequireCurrentScope();
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalScopeId) ||
            propertyId == Guid.Empty ||
            !string.Equals(
                canonicalScopeId,
                currentScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Staff property projection requires valid scoped coordinates.");
        }

        return canonicalScopeId;
    }

    private async Task AcquirePropertyProjectionLockAsync(
        string tenantId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(tenantId, dbContext.CurrentScopeId, StringComparison.Ordinal) ||
            propertyId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A Staff property projection lock requires valid scoped coordinates.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A Staff property projection lock requires an active database transaction.");
        }

        await EfTransactionKeyLock.AcquireAsync(
            dbContext,
            PropertyProjectionLockPrefix + tenantId + ':' + propertyId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }
}
