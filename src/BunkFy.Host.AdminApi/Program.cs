using Gma.Modules.AccessControl.AdminApi;
using Gma.Modules.AccessControl.Application;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Administration.AdminApi;
using Gma.Modules.Administration.Persistence;
using Gma.Modules.Auth.AdminApi;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.AdminApi;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.AdminApi;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.AdminApi;
using Gma.Modules.TaskRuntime.Persistence;
using BunkFy.Modules.Properties.AdminApi;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Inventory.AdminApi;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Reservations.AdminApi;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Guests.AdminApi;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Staff.AdminApi;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Ingestion.AdminApi;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Retention.AdminApi;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.DataRights.AdminApi;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Workspaces.AdminApi;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Adapters.FakeHttp;
using BunkFy.Adapters.ImapReservationMail;
using BunkFy.Adapters.JsonFileDrop;
using BunkFy.Parsers.ReservationMail;
using BunkFy.Extensions.Operations.Notifications;
using BunkFy.Extensions.DataRights.AccessControl;
using BunkFy.Extensions.DataRights.Organizations;
using BunkFy.Extensions.DataRights.TaskRuntime;
using BunkFy.Extensions.Workspaces;
using BunkFy.Host.ServiceDefaults;
using BunkFy.Host.ServiceDefaults.Production;
using BunkFy.Host.ServiceDefaults.Security;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using BunkFy.Host.AdminApi.Security;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.OpenApi;
using Gma.Framework.Api.Production;
using Gma.Framework.Api.Production.EntityFrameworkCore;
using Gma.Framework.Api.Security;
using Gma.Framework.Api.Serilog;
using Gma.Framework.Caching.Cqrs;
using Gma.Framework.Caching.Redis;
using Gma.Framework.Infrastructure;
using Gma.Framework.FileManagement.Minio;
using Gma.Framework.Logging.Serilog;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Messaging.Nats.Aspire;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Security;
using Gma.Framework.Tenancy.Api.Serilog;
using Gma.Framework.Tenancy.Caching;
using Gma.Framework.Tenancy.Messaging.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string authScopeId = builder.Configuration["Auth:GlobalScopeId"] ?? AuthProfile.DefaultGlobalScopeId;
AuthenticationAssuranceRequirement adminOperationAssurance =
    BunkFyAuthenticationAssurance.CreateDestructiveOperationRequirement(builder.Configuration);

builder.Host.UseConfiguredSerilog();
builder.AddBunkFyProductionDeployment(BunkFyDeploymentSurface.AdminApi);
builder.AddBunkFyAuthRetentionProductionAdmission(
    BunkFyDeploymentSurface.AdminApi,
    authComposed: true);
builder.AddBunkFyOrganizationsMaintenanceProductionAdmission(
    BunkFyDeploymentSurface.AdminApi,
    organizationsComposed: true);
builder.AddBunkFyDurableRuntimeProductionAdmission(
    BunkFyDurableRuntimeHostRole.AdminApi,
    taskRuntimeComposed: true);
builder.AddBunkFyOperationsNotificationsProductionAdmission(
    OperationsNotificationsProductionHostRole.AdminApi);

builder.Services.AddGmaAdministrationApi(builder.Configuration);
builder.Services.AddBunkFyAdminApiResourceScopes();
builder.Services.Configure<AdminApiOptions>(
    options => options.AuthenticationAssurance = adminOperationAssurance);
builder.Services.AddBunkFySupportAccess(builder.Configuration);
builder.AddRedisCaching();
builder.AddCachingCqrs();
builder.AddGmaInfrastructure();
builder.AddMinioFileStorage();
builder.AddTenantSerilogRequestLogging();
builder.AddTenantCaching();
builder.AddMessagingInfrastructure();
builder.AddTenantAwareMessaging();
builder.AddConfiguredNatsJetStreamMessaging();
builder.Services.AddApiSecurityDefaults();
builder.Services.AddFakeHttpAdapterDescriptor();
builder.Services.AddImapReservationMailAdapterDescriptor();
builder.Services.AddJsonFileDropAdapterDescriptor();
builder.Services.AddReservationMailParserDescriptor();
builder.Services.AddAccessProfilePermissionAllowlist(WorkspaceAccessRoles.DelegablePermissions);
builder.Services.AddBunkFyOperationsNotifications();
builder.Services.AddBunkFyAccessControlDataRights();
builder.Services.AddBunkFyOrganizationsDataRights();
builder.Services.AddBunkFyTaskRuntimeDataRights();

builder.AddAdminApiModule<AdministrationAdminApiModule>();
builder.AddAdminApiModule<AccessControlAdminApiModule>();
builder.AddAuthAdminApiModule(AuthProfile.Global(authScopeId));
builder.AddAdminApiModule<NotificationsAdminApiModule>();
builder.AddAdminApiModule<OrganizationsAdminApiModule>();
builder.AddAdminApiModule<TaskRuntimeAdminApiModule>();
builder.AddAdminApiModule<PropertiesAdminApiModule>();
builder.AddAdminApiModule<InventoryAdminApiModule>();
builder.AddAdminApiModule<ReservationsAdminApiModule>();
builder.AddAdminApiModule<GuestsAdminApiModule>();
builder.AddAdminApiModule<StaffAdminApiModule>();
builder.AddAdminApiModule<IngestionAdminApiModule>();
builder.AddAdminApiModule<RetentionAdminApiModule>();
builder.AddAdminApiModule<DataRightsAdminApiModule>();
builder.AddAdminApiModule<WorkspacesAdminApiModule>();

builder.AddBunkFyStationK0();
builder.AddServiceDefaults();
builder.AddGmaProductionHttp();
builder.Services.AddGmaEntityFrameworkReadinessCheck<AdminDbContext>("administration-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<AccessControlDbContext>("access-control-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<AuthDbContext>("auth-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<NotificationsDbContext>("notifications-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<OrganizationsDbContext>("organizations-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<TaskRuntimeDbContext>("task-runtime-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<PropertiesDbContext>("properties-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<InventoryDbContext>("inventory-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<ReservationsDbContext>("reservations-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<GuestsDbContext>("guests-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<StaffDbContext>("staff-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<IngestionDbContext>("ingestion-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<RetentionDbContext>("retention-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<DataRightsDbContext>("data-rights-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<WorkspacesDbContext>("workspaces-database");
builder.AddGmaOpenApi();
builder.ValidateModuleComposition();

WebApplication app = builder.Build();

app.UseGmaOpenApi();
app.UseGmaProductionHttp();
app.UseGmaSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapAdminApiModules();
app.ValidateBunkFyStationK0("admin");

app.Run();
