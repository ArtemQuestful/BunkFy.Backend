namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Application.Ports;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffIdentityProvisioningAnchorRepository(
    StaffDbContext dbContext) : IStaffIdentityProvisioningAnchorRepository
{
    public async Task<IReadOnlyList<StaffIdentityProvisioningAnchorRecord>>
        ListAsync(
            IReadOnlyList<StaffIdentityProvisioningSourceKey> sources,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Guid[] workspaceSourceIds = sources
            .Where(source => source.SourceKind ==
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding)
            .Select(source => source.SourceId)
            .ToArray();
        Guid[] membershipSourceIds = sources
            .Where(source => source.SourceKind ==
                StaffIdentityProvisioningSourceKind.OrganizationMembership)
            .Select(source => source.SourceId)
            .ToArray();
        StaffIdentityProvisioningAnchor[] loaded = await dbContext
            .IdentityProvisioningAnchors
            .AsNoTracking()
            .Where(anchor =>
                (anchor.SourceKind ==
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
                 workspaceSourceIds.Contains(anchor.SourceId)) ||
                (anchor.SourceKind ==
                    StaffIdentityProvisioningSourceKind
                        .OrganizationMembership &&
                 membershipSourceIds.Contains(anchor.SourceId)))
            .OrderBy(anchor => anchor.SourceKind)
            .ThenBy(anchor => anchor.SourceId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return loaded.Select(anchor => anchor.ToRecord()).ToArray();
    }

    public async Task<StaffIdentityProvisioningAnchorRecord?> GetAsync(
        StaffIdentityProvisioningSourceKind sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        StaffIdentityProvisioningAnchor? anchor = await dbContext
            .IdentityProvisioningAnchors
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SourceKind == sourceKind &&
                    item.SourceId == sourceId,
                cancellationToken).ConfigureAwait(false);
        return anchor?.ToRecord();
    }

    public Task<bool> HasWorkspaceOnboardingAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) => dbContext
            .IdentityProvisioningAnchors
            .AsNoTracking()
            .AnyAsync(
                anchor => anchor.SourceKind ==
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
                    anchor.StaffMemberId == staffMemberId,
                cancellationToken);

    public Task AddAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        dbContext.IdentityProvisioningAnchors.Add(
            new StaffIdentityProvisioningAnchor(anchor));
        return Task.CompletedTask;
    }
}
