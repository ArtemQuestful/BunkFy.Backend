namespace BunkFy.Modules.Stations.Persistence.PostgreSqlMigrations;

using BunkFy.Modules.Stations.Persistence;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
public sealed class StationsPostgreSqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StationsDbContext>
{
    public StationsDbContext CreateDbContext(string[] args) => new(
        DesignTimeDbContextOptionsFactory.CreatePostgreSqlOptions<StationsDbContext>(args,
            StationsMigrations.PostgreSqlAssembly, StationsMigrations.Schema, StationsMigrations.HistoryTable),
        new DesignTimeScopeContext());
}
