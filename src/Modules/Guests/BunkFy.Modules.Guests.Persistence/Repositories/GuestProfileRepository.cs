namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Persistence.Models;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class GuestProfileRepository(
    GuestsDbContext dbContext,
    IGuestProcessingRestrictionProjectionRepository restrictionProjections)
    : IGuestProfileRepository
{
    public async Task AddUnderAcquiredOperationLockAsync(
        GuestProfile profile,
        CancellationToken cancellationToken)
    {
        bool lockTracked = dbContext.Set<GuestOperationLock>().Local.Any(resourceLock =>
            resourceLock.ResourceKind == GuestOperationLockKind.Guest &&
            resourceLock.ResourceId == profile.Id &&
            string.Equals(resourceLock.ScopeId, profile.ScopeId, StringComparison.Ordinal));
        if (!lockTracked)
        {
            throw new InvalidOperationException(
                "The Guest creation operation lock is not acquired.");
        }

        dbContext.GuestProfiles.Add(profile);
        await restrictionProjections.EnsureAsync(
            profile.ScopeId,
            profile.OriginPropertyId,
            profile.Id,
            profile.CreatedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<GuestProfile?> GetByIdAsync(
        Guid guestId,
        CancellationToken cancellationToken) => dbContext.GuestProfiles
        .FirstOrDefaultAsync(profile => profile.Id == guestId, cancellationToken);

    public Task<GuestProfile?> GetVisibleAsync(
        Guid propertyId,
        Guid guestId,
        CancellationToken cancellationToken) => dbContext.VisibleGuestProfilesAt(propertyId)
        .FirstOrDefaultAsync(profile => profile.Id == guestId, cancellationToken);

    public Task<GuestProfile?> GetForDataRightsAsync(
        Guid propertyId,
        Guid guestId,
        CancellationToken cancellationToken) => dbContext.GuestProfiles.FirstOrDefaultAsync(
        profile => profile.Id == guestId &&
                   (profile.OriginPropertyId == propertyId || dbContext.StayHistory.Any(stay =>
                       stay.GuestId == profile.Id && stay.PropertyId == propertyId)),
        cancellationToken);

    public async Task<GuestListResponse> ListVisibleAsync(
        Guid propertyId,
        string? search,
        GuestStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        IQueryable<GuestProfile> query = dbContext.VisibleGuestProfilesAt(propertyId)
            .AsNoTracking();
        if (status.HasValue)
        {
            GuestProfileState state = status.Value switch
            {
                GuestStatus.Active => GuestProfileState.Active,
                GuestStatus.Archived => GuestProfileState.Archived,
                _ => GuestProfileState.Unknown
            };
            query = query.Where(profile => profile.Status == state);
        }

        string? normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToUpperInvariant();
        if (normalizedSearch is not null)
        {
            query = query.Where(profile =>
                profile.DisplayNameSearch.Contains(normalizedSearch) ||
                (profile.LegalNameSearch != null && profile.LegalNameSearch.Contains(normalizedSearch)) ||
                (profile.EmailSearch != null && profile.EmailSearch.Contains(normalizedSearch)) ||
                (profile.PhoneSearch != null && profile.PhoneSearch.Contains(normalizedSearch)));
        }

        GuestListItemDto[] rows = await query
            .OrderBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(profile => new GuestListItemDto(
                profile.Id,
                profile.DisplayName,
                profile.LegalName,
                profile.Email,
                profile.Phone,
                profile.NationalityCountryCode,
                profile.PreferredLanguageTag,
                profile.Status == GuestProfileState.Active
                    ? GuestStatus.Active
                    : profile.Status == GuestProfileState.Archived
                        ? GuestStatus.Archived
                        : GuestStatus.Unknown,
                profile.LastChangedBy,
                profile.LastChangedAtUtc)
            {
                LanguageTags = profile.LanguageTags
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            rows.Take(pageRequest.PageSize).ToArray(),
            pageRequest.Page,
            pageRequest.PageSize,
            rows.Length > pageRequest.PageSize);
    }

}
