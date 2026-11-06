using BunkFy.Adapters.FakeHttp;
using BunkFy.Adapters.ImapReservationMail;
using BunkFy.Adapters.JsonFileDrop;
using BunkFy.Adapters.SmtpEmail;
using BunkFy.Extensions.DataRights.AccessControl;
using BunkFy.Extensions.DataRights.Organizations;
using BunkFy.Extensions.DataRights.TenantTermination;
using BunkFy.Extensions.Operations.Notifications;
using BunkFy.Extensions.ReservationGuestRecords;
using BunkFy.Extensions.Workspaces;
using BunkFy.Host.Api;
using BunkFy.Host.ServiceDefaults;
using BunkFy.Host.ServiceDefaults.Production;
using BunkFy.Host.ServiceDefaults.Security;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using BunkFy.Modules.DataRights.Api;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Api;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Api;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Api;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Api;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Api;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Api;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Staff.Api;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Stations.Api;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Parsers.ReservationMail;
using Gma.Extensions.Auth.Notifications;
using Gma.Extensions.Auth.Organizations;
using Gma.Extensions.Organizations.Tenancy;
using Gma.Framework.Api.Modules;
using Gma.Framework.Api.OpenApi;
using Gma.Framework.Api.Production;
using Gma.Framework.Api.Production.EntityFrameworkCore;
using Gma.Framework.Api.Security;
using Gma.Framework.Api.Serilog;
using Gma.Framework.Caching.Cqrs;
using Gma.Framework.Caching.Redis;
using Gma.Framework.FileManagement.Minio;
using Gma.Framework.Infrastructure;
using Gma.Framework.Logging.Serilog;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Messaging.Nats.Aspire;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Notifications.Api;
using Gma.Framework.Notifications.Cqrs;
using Gma.Framework.Notifications.SignalR;
using Gma.Framework.RateLimiting.Redis;
using Gma.Framework.Realtime.Notifications;
using Gma.Framework.Security;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Gma.Framework.Tenancy.Api.Serilog;
using Gma.Framework.Tenancy.Caching;
using Gma.Framework.Tenancy.Messaging.Infrastructure;
using Gma.Modules.AccessControl.Api;
using Gma.Modules.AccessControl.Application;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Api;
using Gma.Modules.Auth.Authenticators.Totp;
using Gma.Modules.Auth.Application.Ports;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Auth.Providers.OpenIdConnect;
using Gma.Modules.Notifications.Adapters.Email;
using Gma.Modules.Notifications.Api;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Api;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.Tenancy.Api;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string authScopeId = builder.Configuration["Auth:GlobalScopeId"] ?? AuthProfile.DefaultGlobalScopeId;
AuthProfile authProfile = AuthProfile.Global(authScopeId);
AuthenticationAssuranceRequirement privilegedOperationAssurance =
    BunkFyAuthenticationAssurance.CreatePrivilegedOperationRequirement(builder.Configuration);
AuthenticationAssuranceRequirement destructiveOperationAssurance =
    BunkFyAuthenticationAssurance.CreateDestructiveOperationRequirement(builder.Configuration);
bool stationHttpEnabled = builder.Configuration.GetValue<bool>("Stations:Http:Enabled");
if (stationHttpEnabled && builder.Configuration.GetValue<bool>("Station:Enabled"))
{
    throw new InvalidOperationException("The legacy station experiment cannot be enabled with Stations HTTP.");
}
bool workspaceStaffOnboardingRecoveryPostEnabled = builder.Configuration
    .GetValue<bool>("BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled");
if (builder.Environment.IsProduction() &&
    workspaceStaffOnboardingRecoveryPostEnabled)
{
    throw new InvalidOperationException(
        "Workspace staff onboarding recovery POST cannot be enabled in Production while CP3 privacy blockers remain open.");
}

builder.Host.UseConfiguredSerilog();
builder.AddBunkFyProductionDeployment(BunkFyDeploymentSurface.PublicApi);
builder.AddBunkFyAuthRetentionProductionAdmission(
    BunkFyDeploymentSurface.PublicApi,
    authComposed: true);
builder.AddBunkFyOrganizationsMaintenanceProductionAdmission(
    BunkFyDeploymentSurface.PublicApi,
    organizationsComposed: true);
builder.AddBunkFyDurableRuntimeProductionAdmission(
    BunkFyDurableRuntimeHostRole.PublicApi,
    taskRuntimeComposed: false);
builder.AddBunkFyOperationsNotificationsProductionAdmission(
    OperationsNotificationsProductionHostRole.PublicApi);

builder.AddUserNotificationsCqrs();
builder.AddUserNotificationsRealtime();
builder.AddRedisCaching();
if (builder.Configuration.GetValue<bool>("Ingestion:AdapterIngress:Enabled") ||
    builder.Configuration.GetValue<HttpRateLimitMode>("Http:RateLimiting:Mode") ==
        HttpRateLimitMode.Distributed)
{
    builder.AddRedisRateLimiting();
}
builder.AddCachingCqrs();
builder.AddGmaInfrastructure();
builder.AddGmaProductionDataProtection();
builder.Services.AddSingleton<WorkspaceJoinBrowserContinuation>();
builder.AddBunkFyCountryPolicies();
builder.AddTenantSerilogRequestLogging();
builder.AddTenantCaching();
builder.AddMinioFileStorage();
builder.AddMessagingInfrastructure();
builder.AddTenantAwareMessaging();
builder.AddConfiguredNatsJetStreamMessaging();
builder.AddUserNotificationServerSentEvents();
builder.AddUserNotificationSignalR();
builder.Services.AddApiSecurityDefaults();
builder.Services.AddGmaTenantAccessControlAspNetCore();
builder.Services.AddFakeHttpAdapterDescriptor();
builder.Services.AddImapReservationMailAdapterDescriptor();
builder.Services.AddJsonFileDropAdapterDescriptor();
builder.Services.AddReservationMailParserDescriptor();

builder.Services.AddAccessProfilePermissionAllowlist(WorkspaceAccessRoles.DelegablePermissions);
builder.Services.Configure<AccessControlApiSecurityOptions>(
    options => options.ProfileManagementAssurance = privilegedOperationAssurance);
builder.Services.Configure<OrganizationsApiSecurityOptions>(
    options => options.GovernanceOperationsAssurance = privilegedOperationAssurance);
builder.Services.Configure<WorkspacesApiSecurityOptions>(options =>
{
    options.StaffOnboardingManagementAssurance =
        privilegedOperationAssurance;
    options.StaffOnboardingRecoveryPostEnabled =
        workspaceStaffOnboardingRecoveryPostEnabled;
});
builder.Services.Configure<PropertiesApiSecurityOptions>(options =>
{
    options.TimeZoneManagementAssurance = privilegedOperationAssurance;
    options.ProcessingActivationAssurance = privilegedOperationAssurance;
    options.PropertyRetirementAssurance = privilegedOperationAssurance;
});
builder.Services.Configure<InventoryApiSecurityOptions>(options =>
    options.TopologyRetirementAssurance = privilegedOperationAssurance);
builder.Services.Configure<IngestionApiSecurityOptions>(options =>
{
    options.CredentialManagementAssurance = privilegedOperationAssurance;
    options.CheckpointResetAssurance = privilegedOperationAssurance;
    options.IngressResumeAssurance = privilegedOperationAssurance;
});
builder.Services.Configure<StaffApiSecurityOptions>(
    options =>
    {
        options.AccountLinkManagementAssurance =
            privilegedOperationAssurance;
        options.EmploymentGovernanceAssurance =
            privilegedOperationAssurance;
        options.DataHoldReleaseAssurance =
            privilegedOperationAssurance;
    });
builder.Services.Configure<ReservationsApiSecurityOptions>(options =>
    options.CorrectionExecutionAssurance = privilegedOperationAssurance);
builder.Services.Configure<RetentionApiSecurityOptions>(options =>
    options.RetryAssurance = privilegedOperationAssurance);
builder.Services.Configure<GuestsApiSecurityOptions>(
    options =>
    {
        options.CorrectionExecutionAssurance =
            privilegedOperationAssurance;
        options.RestrictionExecutionAssurance =
            privilegedOperationAssurance;
        options.DataHoldReleaseAssurance =
            privilegedOperationAssurance;
    });
builder.Services.Configure<DataRightsApiSecurityOptions>(
    options =>
    {
        options.AnonymisationExecutionAssurance = destructiveOperationAssurance;
        options.RestrictionExecutionAssurance = privilegedOperationAssurance;
        options.CorrectionExecutionAssurance = privilegedOperationAssurance;
        options.ExportGenerationAssurance = privilegedOperationAssurance;
        options.ExportDownloadAssurance = destructiveOperationAssurance;
    });
builder.AddModule<AccessControlApiModule>();

builder.AddModule<TenancyModule>();
builder.AddAuthModule(authProfile);
builder.AddAuthTotpAuthenticator();
builder.AddAuthOpenIdConnectProviders();
builder.AddModule<NotificationsModule>();
builder.AddModule<OrganizationsModule>();
builder.Services.AddAuthNotificationsExtension(options => options.FixedAuthScopeId = authScopeId);
builder.Services.AddAuthOrganizationsExtension(options => options.GlobalAuthScopeId = authScopeId);
builder.Services.AddOrganizationsTenancyExtension();
builder.Services.AddBunkFyWorkspaces(options => options.GlobalAuthScopeId = authScopeId);
builder.Services.AddBunkFyWorkspaceAdmission(builder.Configuration, builder.Environment.IsProduction());
builder.Services.Replace(ServiceDescriptor.Scoped<INotificationUserScopeAuthorizer, WorkspaceNotificationUserScopeAuthorizer>());
builder.Services.AddBunkFyAccessControlDataRights();
builder.Services.AddBunkFyOrganizationsDataRights();
builder.Services.AddBunkFyTenantTerminationOperatorCatalog();
builder.Services.AddBunkFyOperationsNotifications();
builder.Services.AddBunkFyReservationGuestRecords();
builder.Services.AddBunkFyWorkspaceOwnerNotificationAudience();
builder.Services.AddBunkFySmtpEmailSender(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddNotificationEmailAdapter(builder.Configuration);
builder.AddModule<PropertiesModule>();
builder.AddModule<InventoryModule>();
builder.AddModule<ReservationsModule>();
builder.AddModule<GuestsModule>();
builder.AddModule<StaffModule>();
builder.Services.Replace(ServiceDescriptor.Scoped<IRememberedBrowserAccountProfileReader, StaffRememberedBrowserAccountProfileReader>());
builder.AddModule<WorkspacesModule>();
builder.AddModule<IngestionModule>();
builder.Services.AddBunkFyOperationsIngestionNotifications();
builder.AddModule<DataRightsModule>();
builder.AddModule<RetentionModule>();
builder.AddModule<StationsModule>();
if (stationHttpEnabled)
{
    StationHttpHostAdmission.Validate(builder.Configuration);
    builder.Services.AddStationsManagement(destructiveOperationAssurance, authScopeId);
    builder.Services.AddSingleton<IStationPepperProvider, ConfiguredStationPepperProvider>();
}
// module-scaffold:public-api-modules

// Preserve the earlier disabled experiment without composing it with the opaque-cookie runtime.
if (!stationHttpEnabled)
{
    builder.AddBunkFyStationK0();
}
builder.AddServiceDefaults();
builder.AddGmaProductionHttp();
builder.Services.AddGmaEntityFrameworkReadinessCheck<AccessControlDbContext>("access-control-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<AuthDbContext>("auth-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<NotificationsDbContext>("notifications-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<OrganizationsDbContext>("organizations-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<PropertiesDbContext>("properties-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<InventoryDbContext>("inventory-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<ReservationsDbContext>("reservations-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<GuestsDbContext>("guests-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<StaffDbContext>("staff-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<WorkspacesDbContext>("workspaces-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<IngestionDbContext>("ingestion-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<DataRightsDbContext>("data-rights-database");
builder.Services.AddGmaEntityFrameworkReadinessCheck<RetentionDbContext>("retention-database");
builder.AddGmaOpenApi();
builder.Services.AddSwaggerGen(options => options.SchemaFilter<RetirementContextSchemaFilter>());
builder.Services.AddSwaggerGen(options => options.SchemaFilter<StationPinSchemaFilter>());
builder.Services.AddSwaggerGen(options => options.SchemaFilter<RememberedBrowserSchemaFilter>());
builder.Services.AddSwaggerGen(options =>
{
    options.SchemaFilter<WorkspaceJoinBrowserOpenApiFilter>();
    options.OperationFilter<WorkspaceJoinBrowserOpenApiFilter>();
});
builder.ValidateModuleComposition();

WebApplication app = builder.Build();

app.UseGmaOpenApi();
app.UseGmaProductionHttp();
app.UseGmaSerilogRequestLogging();
app.UseStationHttpSecurity();
app.UseAuthRememberedBrowserSecurity();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/api/smoke", (IOptions<BunkFyDeploymentOptions> deployment) => Results.Ok(new
{
    Application = "BunkFy",
    Service = "BunkFy.Host.Api",
    Status = "ok",
    ReleaseId = string.IsNullOrWhiteSpace(deployment.Value.ReleaseId)
        ? "local-unversioned"
        : deployment.Value.ReleaseId,
    deployment.Value.AdmissionEvidenceReference,
    TimestampUtc = DateTimeOffset.UtcNow
}));
app.MapBunkFyProductCapabilities();
app.MapModules();
app.MapBunkFyAccessPermissionEndpoints();
app.MapWorkspaceJoinBrowserEndpoints();
app.MapBunkFyReservationGuestRecordEndpoints();
app.MapUserNotificationServerSentEvents();
app.MapUserNotificationSignalR();
if (!stationHttpEnabled)
{
    app.ValidateBunkFyStationK0("public");
}

app.Run();
