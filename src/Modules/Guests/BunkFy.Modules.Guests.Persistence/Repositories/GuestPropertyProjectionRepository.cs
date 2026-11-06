namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed class GuestPropertyProjectionRepository(
    GuestsDbContext dbContext,
    IGuestOperationLock operationLock)
    : IGuestPropertyProjectionRepository
{
    public async Task ApplyTopologyAsync(
        GuestPropertyTopologyWriteModel property,
        CancellationToken cancellationToken)
    {
        await operationLock.AcquirePropertiesAsync(
            property.ScopeId,
            [property.PropertyId],
            cancellationToken).ConfigureAwait(false);
        GuestPropertyProjection current = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        current.ApplyTopology(
            property.Name,
            property.TimeZoneId,
            property.Status,
            property.SourceVersion);
    }

    public async Task ApplyPolicyAsync(
        GuestPropertyPolicyWriteModel property,
        CancellationToken cancellationToken)
    {
        await operationLock.AcquirePropertiesAsync(
            property.ScopeId,
            [property.PropertyId],
            cancellationToken).ConfigureAwait(false);
        GuestPropertyProjection current = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        current.ApplyPolicy(property.ProcessingStatus, property.GovernancePolicy, property.SourceVersion);
    }

    public async Task ApplyTimeZoneAsync(
        GuestPropertyTimeZoneWriteModel property,
        CancellationToken cancellationToken)
    {
        await operationLock.AcquirePropertiesAsync(
            property.ScopeId,
            [property.PropertyId],
            cancellationToken).ConfigureAwait(false);
        GuestPropertyProjection current = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        current.ApplyTimeZone(
            property.TimeZoneId,
            property.CanonicalTimeZoneId,
            property.TimeZoneStatus,
            property.TimeZoneCatalogVersion,
            property.TimeZoneEvidenceSource,
            property.TimeZoneEvidenceSourceVersion);
    }

    public async Task<GuestPropertyPolicySnapshot?> GetPolicyAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        GuestPropertyProjection? property = await dbContext.PropertyProjections
            .AsNoTracking()
            .Include(item => item.GovernancePolicy)
            .ThenInclude(policy => policy!.Acknowledgements)
            .FirstOrDefaultAsync(
                item => item.Id == propertyId,
                cancellationToken)
            .ConfigureAwait(false);
        return property is null
            ? null
            : new GuestPropertyPolicySnapshot(
                property.IsKnown,
                property.Status == PropertyStatus.Active,
                property.ProcessingStatus,
                property.GovernancePolicy.ToContract(),
                property.TopologySourceVersion,
                property.PolicySourceVersion);
    }

    private async Task<GuestPropertyProjection> GetOrCreateAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        GuestPropertyProjection? current = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.ScopeId == scopeId && item.Id == propertyId) ??
            await dbContext.PropertyProjections.FirstOrDefaultAsync(
                item => item.ScopeId == scopeId && item.Id == propertyId,
                cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            return current;
        }

        current = new GuestPropertyProjection(
            scopeId,
            propertyId,
            null,
            timeZoneId: null,
            PropertyStatus.Unknown,
            0);
        dbContext.PropertyProjections.Add(current);
        return current;
    }
}
