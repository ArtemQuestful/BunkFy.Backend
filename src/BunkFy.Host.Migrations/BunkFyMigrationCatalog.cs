namespace BunkFy.Host.Migrations;

using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Stations.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Administration.Persistence;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed record BunkFyMigrationModule(string Name, DbContext Context);

public static class BunkFyMigrationCatalog
{
    public static IReadOnlyList<BunkFyMigrationModule> Resolve(
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        List<BunkFyMigrationModule> modules =
        [
            Resolve<AdminDbContext>(serviceProvider, "administration"),
            Resolve<AccessControlDbContext>(serviceProvider, "access-control"),
            Resolve<AuthDbContext>(serviceProvider, "auth"),
            Resolve<NotificationsDbContext>(serviceProvider, "notifications"),
            Resolve<OrganizationsDbContext>(serviceProvider, "organizations"),
            Resolve<TaskRuntimeDbContext>(serviceProvider, "task-runtime"),
            Resolve<DataRightsDbContext>(serviceProvider, "data-rights"),
            Resolve<PropertiesDbContext>(serviceProvider, "properties"),
            Resolve<InventoryDbContext>(serviceProvider, "inventory"),
            Resolve<ReservationsDbContext>(serviceProvider, "reservations"),
            Resolve<GuestsDbContext>(serviceProvider, "guests"),
            Resolve<StaffDbContext>(serviceProvider, "staff"),
            Resolve<WorkspacesDbContext>(serviceProvider, "workspaces"),
            Resolve<IngestionDbContext>(serviceProvider, "ingestion"),
            Resolve<RetentionDbContext>(serviceProvider, "retention")
        ];
        if (serviceProvider.GetService<StationsDbContext>() is { } stations)
        {
            modules.Add(new("stations", stations));
        }
        return modules;
    }

    private static BunkFyMigrationModule Resolve<TContext>(
        IServiceProvider serviceProvider,
        string name)
        where TContext : DbContext =>
        new(name, serviceProvider.GetRequiredService<TContext>());
}
