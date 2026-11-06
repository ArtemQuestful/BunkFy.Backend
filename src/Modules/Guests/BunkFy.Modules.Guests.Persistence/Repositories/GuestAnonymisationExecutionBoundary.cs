namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Models;
using Microsoft.EntityFrameworkCore;

internal sealed class GuestAnonymisationExecutionBoundary(
    GuestsDbContext dbContext,
    IGuestOperationLock operationLock)
    : IGuestAnonymisationExecutionBoundary
{
    public async Task AcquireAsync(
        string tenantId,
        Guid guestId,
        CancellationToken cancellationToken)
    {
        string scopeId = dbContext.CurrentScopeId;
        if (!string.Equals(tenantId, scopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Guest anonymisation execution boundary requires the active tenant scope.");
        }

        await operationLock.AcquireGuestAsync(
            tenantId,
            guestId,
            cancellationToken).ConfigureAwait(false);

        Guid? originPropertyId = await dbContext.GuestProfiles.AsNoTracking()
            .Where(profile =>
                profile.ScopeId == scopeId &&
                profile.Id == guestId)
            .Select(profile => (Guid?)profile.OriginPropertyId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!originPropertyId.HasValue)
        {
            return;
        }

        Guid[] stayPropertyIds = await dbContext.StayHistory.AsNoTracking()
            .Where(stay =>
                stay.ScopeId == scopeId &&
                stay.GuestId == guestId)
            .Select(stay => stay.PropertyId)
            .Distinct()
            .Take(GuestAnonymisationEligibilityContract.MaximumAffectedProperties + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        Guid[] holdPropertyIds = await dbContext.DataHolds.AsNoTracking()
            .Where(hold =>
                hold.ScopeId == scopeId &&
                hold.GuestId == guestId &&
                hold.State == GuestDataHoldState.Active)
            .Select(hold => hold.PropertyId)
            .Distinct()
            .Take(GuestAnonymisationEligibilityContract.MaximumAffectedProperties + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        Guid[] propertyIds = stayPropertyIds
            .Append(originPropertyId.Value)
            .Concat(holdPropertyIds)
            .Distinct()
            .OrderBy(propertyId => propertyId)
            .Take(GuestAnonymisationEligibilityContract.MaximumAffectedProperties + 1)
            .ToArray();

        await operationLock.AcquirePropertiesAsync(
            tenantId,
            propertyIds,
            cancellationToken).ConfigureAwait(false);
    }
}
