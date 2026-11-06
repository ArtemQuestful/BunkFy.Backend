namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Application.Ports;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffIdentityProvisioningAnchorResolutionRepository(
    StaffDbContext dbContext)
    : IStaffIdentityProvisioningAnchorResolutionRepository
{
    public async Task<IReadOnlyList<
        StaffIdentityProvisioningAnchorResolutionRecord>> ListAsync(
            IReadOnlyList<Guid> sourceIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        StaffIdentityProvisioningAnchorResolution[] resolutions =
            await dbContext.IdentityProvisioningAnchorResolutions
                .AsNoTracking()
                .Where(resolution => resolution.SourceKind ==
                        StaffIdentityProvisioningSourceKind
                            .WorkspaceOnboarding &&
                    sourceIds.Contains(resolution.SourceId))
                .OrderBy(resolution => resolution.SourceId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return resolutions.Select(resolution => resolution.ToRecord())
            .ToArray();
    }

    public async Task<StaffIdentityProvisioningAnchorResolutionRecord?>
        GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
    {
        StaffIdentityProvisioningAnchorResolution? resolution =
            await dbContext.IdentityProvisioningAnchorResolutions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.SourceKind ==
                        StaffIdentityProvisioningSourceKind
                            .WorkspaceOnboarding &&
                        item.SourceId == sourceId,
                    cancellationToken).ConfigureAwait(false);
        return resolution?.ToRecord();
    }

    public Task<bool> HasUnresolvedWorkspaceOnboardingAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        dbContext.IdentityProvisioningAnchors
            .AsNoTracking()
            .Where(anchor => anchor.SourceKind ==
                    StaffIdentityProvisioningSourceKind
                        .WorkspaceOnboarding &&
                anchor.StaffMemberId == staffMemberId)
            .AnyAsync(
                anchor => !dbContext.IdentityProvisioningAnchorResolutions
                    .Any(resolution =>
                        resolution.SourceKind == anchor.SourceKind &&
                        resolution.SourceId == anchor.SourceId &&
                        resolution.StaffMemberId == anchor.StaffMemberId &&
                        resolution.ResolutionEventId ==
                            anchor.ResolutionEventId),
                cancellationToken);

    public Task AddAsync(
        StaffIdentityProvisioningAnchorResolutionRecord resolution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        dbContext.IdentityProvisioningAnchorResolutions.Add(
            new StaffIdentityProvisioningAnchorResolution(resolution));
        return Task.CompletedTask;
    }
}
