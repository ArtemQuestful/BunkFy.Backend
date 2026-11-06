namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.Stations.Application;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddStationsPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.Equals(builder.Configuration["Persistence:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Stations core requires PostgreSQL before registration.");
        }

        string connection = builder.Configuration.GetConnectionString("PostgreSql") ??
            throw new InvalidOperationException("Stations PostgreSQL connection is missing.");
        builder.Services.TryAddModuleDbContext<StationsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsAssembly(StationsMigrations.PostgreSqlAssembly)
                .MigrationsHistoryTable(StationsMigrations.HistoryTable, StationsMigrations.Schema)));
        builder.Services.TryAddScoped<StationsStore>();
        builder.Services.TryAddScoped<IStationsStore>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationCredentialBootstrap>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationRuntimeStore>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationManagementStore>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationJobGrantReader>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationWorkspaceSettingsReader>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationWorkspaceSettingsStore>(s => s.GetRequiredService<StationsStore>());
        builder.Services.TryAddScoped<IStationPairingHandoffReader>(s => s.GetRequiredService<StationsStore>());
        return builder;
    }
}
