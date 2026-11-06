namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Stations.Domain;
using Gma.Framework.Persistence.EntityFrameworkCore;

/// <summary>Short P1 critical sections serialize Stations writes per tenant; no cross-module transaction guarantee.</summary>
internal static class StationsTenantMutationLock
{
    public static async Task AcquireAsync(StationsDbContext db, string tenant, CancellationToken ct)
    {
        StationRules.Coordinates(tenant);
        db.RequirePostgreSql();
        await EfTransactionKeyLock.AcquireAsync(db, TenantTerminationCoordination.CreateTenantMutationResource(tenant),
            EfTransactionKeyLockMode.Shared, ct).ConfigureAwait(false);
        await EfTransactionKeyLock.AcquireAsync(db, "bunkfy:stations:core:" + tenant,
            EfTransactionKeyLockMode.Exclusive, ct).ConfigureAwait(false);
    }
}
