namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.Stations.Application;
using Microsoft.EntityFrameworkCore;

internal sealed partial class StationsStore
{
    public async Task<StationWorkspaceSettings> ReadWorkspaceSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!db.ScopeFilterEnabled || string.IsNullOrWhiteSpace(db.CurrentScopeId))
        {
            return new(false, 0);
        }

        db.RequirePostgreSql();
        StationsTenantLifecycleState? lifecycle = await db.Lifecycle
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return lifecycle is null
            ? new(true, 0)
            : new(lifecycle.SharedStationPinEnabled, lifecycle.Revision);
    }

    public async Task<StationWorkspaceSettingsUpdateResult> UpdateWorkspaceSettingsAsync(
        bool sharedStationPinEnabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0 || !db.ScopeFilterEnabled || string.IsNullOrWhiteSpace(db.CurrentScopeId) ||
            db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
        {
            return Unavailable();
        }

        db.RequirePostgreSql();
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await StationsTenantMutationLock.AcquireAsync(
                db,
                db.CurrentScopeId,
                cancellationToken)
            .ConfigureAwait(false);

        StationsTenantLifecycleState? lifecycle = await db.Lifecycle
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var lifecycleRevision = expectedRevision;
        if (lifecycle is null)
        {
            if (expectedRevision != 0)
            {
                return WorkspaceSettingsConflict(new(true, 0));
            }

            lifecycle = new(db.CurrentScopeId);
            db.Lifecycle.Add(lifecycle);
            // New lifecycle rows start at revision 1. The public workspace
            // settings resource uses revision 0 for the absent row, so the
            // first write must compare against the entity's initial revision.
            lifecycleRevision = lifecycle.Revision;
        }

        if (!lifecycle.TrySetSharedStationPinEnabled(
                sharedStationPinEnabled,
                lifecycleRevision))
        {
            return WorkspaceSettingsConflict(new(
                lifecycle.SharedStationPinEnabled,
                lifecycle.Revision));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            StationWorkspaceSettingsUpdateState.Applied,
            new(lifecycle.SharedStationPinEnabled, lifecycle.Revision));
    }

    private static StationWorkspaceSettingsUpdateResult WorkspaceSettingsConflict(
        StationWorkspaceSettings settings) => new(
            StationWorkspaceSettingsUpdateState.Conflict,
            settings);

    private static StationWorkspaceSettingsUpdateResult Unavailable() => new(
        StationWorkspaceSettingsUpdateState.Unavailable,
        new(false, 0));
}
