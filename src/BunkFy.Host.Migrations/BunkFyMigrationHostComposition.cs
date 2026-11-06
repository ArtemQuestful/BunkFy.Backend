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
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Administration.Persistence;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

public static class BunkFyMigrationHostComposition
{
    public static HostApplicationBuilder AddBunkFyMigrationPersistence(
        this HostApplicationBuilder builder,
        string authScopeId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authScopeId);

        builder.Services.AddScoped<IScopeContext, DesignTimeScopeContext>();
        builder.AddAdministrationPersistence();
        builder.AddAccessControlPersistence();
        builder.AddAuthPersistence(AuthProfile.Global(authScopeId));
        builder.AddNotificationsPersistence();
        builder.AddOrganizationsPersistence();
        builder.AddTaskRuntimePersistence();
        builder.AddDataRightsPersistence();
        builder.AddPropertiesPersistence();
        builder.AddInventoryPersistence();
        builder.AddReservationsPersistence();
        builder.AddGuestsPersistence();
        builder.AddStaffPersistence();
        builder.AddWorkspacesPersistence();
        builder.AddIngestionPersistence();
        builder.AddRetentionPersistence();
        if (builder.Configuration.GetValue<bool>("Stations:Http:Enabled"))
        {
            builder.AddStationsPersistence();
        }
        builder.Services.RemoveAll<IHostedService>();
        return builder;
    }
}
