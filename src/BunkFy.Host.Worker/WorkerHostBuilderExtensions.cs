namespace BunkFy.Host.Worker;

using BunkFy.Adapters.Configuration;
using BunkFy.Adapters.FakeHttp;
using BunkFy.Adapters.ImapReservationMail;
using BunkFy.Adapters.JsonFileDrop;
using BunkFy.Adapters.SmtpEmail;
using BunkFy.Extensions.DataRights.AccessControl;
using BunkFy.Extensions.DataRights.Organizations;
using BunkFy.Extensions.DataRights.TaskRuntime;
using BunkFy.Extensions.DataRights.TenantTermination;
using BunkFy.Extensions.Operations.Notifications;
using BunkFy.Extensions.Retention.TaskRuntime;
using BunkFy.Extensions.Workspaces;
using BunkFy.Host.ServiceDefaults;
using BunkFy.Host.ServiceDefaults.Production;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Ingress;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Application;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Parsers.ReservationMail;
using Gma.Extensions.Auth.Notifications;
using Gma.Extensions.Auth.Organizations;
using Gma.Framework.Caching.Cqrs;
using Gma.Framework.Caching.Redis;
using Gma.Framework.FileManagement.Minio;
using Gma.Framework.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Messaging.Nats.Aspire;
using Gma.Framework.ModuleComposition;
using Gma.Framework.RateLimiting.Redis;
using Gma.Framework.Tasks.Cqrs;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy.Caching;
using Gma.Framework.Tenancy.Messaging.Infrastructure;
using Gma.Framework.Tenancy.Tasks;
using Gma.Modules.AccessControl.Application;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Infrastructure.TokenHashing;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.Adapters.Email;
using Gma.Modules.Notifications.Application;
using Gma.Modules.Notifications.Contracts;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Application;
using Gma.Modules.Organizations.Contracts;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Application;
using Gma.Modules.TaskRuntime.Contracts;
using Gma.Modules.TaskRuntime.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class WorkerHostBuilderExtensions
{
    public static IHostApplicationBuilder AddWorkerHost(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        WorkerHostOptions workerOptions = WorkerHostOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(workerOptions);
        builder.AddBunkFyProductionDeployment(
            BunkFyDeploymentSurface.Worker,
            fileManagementEnabled: workerOptions.Modules.Ingestion);
        builder.AddBunkFyAuthRetentionProductionAdmission(
            BunkFyDeploymentSurface.Worker,
            workerOptions.Modules.Auth);
        builder.AddBunkFyOrganizationsMaintenanceProductionAdmission(
            BunkFyDeploymentSurface.Worker,
            workerOptions.Modules.Organizations);
        builder.AddBunkFyDurableRuntimeProductionAdmission(
            BunkFyDurableRuntimeHostRole.Worker,
            workerOptions.Modules.TaskRuntime);
        builder.AddBunkFyOperationsNotificationsProductionAdmission(
            OperationsNotificationsProductionHostRole.Worker,
            workerOptions.Modules.Notifications);
        builder.AddBunkFyCountryPolicies();

        builder.AddRedisCaching();
        if (workerOptions.Modules.Ingestion && HasRedisRateLimitingConnection(builder))
        {
            builder.AddRedisRateLimiting();
        }

        builder.AddCachingCqrs();
        builder.AddGmaInfrastructure();
        builder.AddTenantCaching();
        builder.AddMessagingInfrastructure();
        builder.AddTenantAwareMessaging();
        builder.AddConfiguredNatsJetStreamMessaging();

        if (workerOptions.NatsConsumersEnabled)
        {
            builder.AddConfiguredNatsJetStreamConsumers();
        }

        AddConfiguredModuleGroups(builder, workerOptions);

        if (RequiresWorkspaceTerminationAdmission(workerOptions))
        {
            builder.AddWorkspacesTerminationAdmissionPersistence();
        }

        if (workerOptions.TaskWorkerEnabled)
        {
            builder.AddTenantTaskExecutionContext();
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Scoped<
                    ITaskExecutionContextContributor,
                    TenantTerminationGlobalTaskExecutionContextContributor>());
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Scoped<
                    ITaskExecutionContextContributor,
                    WorkspaceTerminationTaskExecutionContextContributor>());
            builder.AddTaskCqrs();
            builder.AddTaskWorkerRuntime();
            builder.AddTaskRunScheduling();
        }

        builder.AddBunkFyTenantTerminationProductionAdmission(
            workerOptions.Modules.DataRights,
            HasCompleteTenantTerminationOwnerTopology(workerOptions.Modules),
            workerOptions.TaskWorkerEnabled);

        builder.AddServiceDefaults();
        return builder;
    }

    public static IHost LogWorkerStartupSummary(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        WorkerHostOptions workerOptions = host.Services.GetRequiredService<WorkerHostOptions>();
        ILogger logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("BunkFy.Host.Worker");
        string moduleList = workerOptions.GetComposedModuleNames() is { Count: > 0 } modules
            ? string.Join(", ", modules)
            : "none";

        logger.LogInformation(
            "BunkFy.Host.Worker starting. NATS publishing enabled: {NatsPublishingEnabled}; NATS consumers enabled: {NatsConsumersEnabled}; task workers enabled: {TaskWorkerEnabled}; composed modules: {WorkerModules}.",
            workerOptions.NatsPublishingEnabled,
            workerOptions.NatsConsumersEnabled,
            workerOptions.TaskWorkerEnabled,
            moduleList);

        return host;
    }

    private static void AddConfiguredModuleGroups(IHostApplicationBuilder builder, WorkerHostOptions workerOptions)
    {
        string authScopeId = builder.Configuration["Auth:GlobalScopeId"] ?? AuthProfile.DefaultGlobalScopeId;
        AuthProfile authProfile = AuthProfile.Global(authScopeId);

        if (workerOptions.Modules.Auth)
        {
            builder.SelectModuleProfile(authProfile.Descriptor, "BunkFy.Host.Worker/Auth");
            builder.Services.AddAuthTokenHashingInfrastructure(builder.Configuration);
            builder.AddAuthPersistence(authProfile);
        }

        if (workerOptions.Modules.AccessControl)
        {
            builder.SelectModuleProfile(AccessControlProfiles.Default, "BunkFy.Host.Worker/AccessControl");
            builder.Services.AddAccessProfilePermissionAllowlist(WorkspaceAccessRoles.DelegablePermissions);
            builder.Services.AddAccessControlApplication(builder.Configuration);
            builder.AddAccessControlPersistence();
        }

        if (workerOptions.Modules.Notifications)
        {
            builder.SelectModuleProfile(NotificationsProfiles.Default, "BunkFy.Host.Worker/Notifications");
            builder.Services.AddNotificationsApplication(builder.Configuration);
            builder.AddNotificationsPersistence();
            builder.Services.AddBunkFySmtpEmailSender(
                builder.Configuration,
                builder.Environment.IsProduction());
            builder.Services.AddNotificationEmailAdapter(builder.Configuration);

            if (workerOptions.Modules.Auth)
            {
                builder.Services.AddAuthNotificationsExtension(
                    options => options.FixedAuthScopeId = authScopeId);
            }

            if (workerOptions.Modules.Staff && workerOptions.Modules.Organizations)
            {
                builder.Services.AddBunkFyOperationsNotifications();
                if (workerOptions.Modules.AccessControl)
                {
                    builder.Services.AddBunkFyWorkspaceOwnerNotificationAudience();
                }
            }
        }

        if (workerOptions.Modules.Organizations)
        {
            builder.SelectModuleProfile(
                OrganizationsProfiles.Default,
                "BunkFy.Host.Worker/Organizations");
            builder.Services.AddOrganizationsApplication(builder.Configuration);
            builder.AddOrganizationsPersistence();

            if (workerOptions.Modules.Auth)
            {
                builder.Services.AddAuthOrganizationsExtension(
                    options => options.GlobalAuthScopeId = authScopeId);
            }

            if (workerOptions.Modules.Auth &&
                workerOptions.Modules.AccessControl &&
                workerOptions.Modules.Staff)
            {
                builder.Services.AddBunkFyWorkspaces(
                    options => options.GlobalAuthScopeId = authScopeId);
                builder.SelectModuleProfile(
                    WorkspacesProfiles.Default,
                    "BunkFy.Host.Worker/Workspaces");
                builder.Services.AddWorkspacesApplication(
                    builder.Configuration,
                    authScopeId);
                if (workerOptions.TaskWorkerEnabled)
                {
                    builder.Services.AddWorkspacesTaskHandlers();
                }
                builder.AddWorkspacesPersistence();
            }
        }

        if (workerOptions.Modules.Properties)
        {
            builder.SelectModuleProfile(PropertiesProfiles.Default, "BunkFy.Host.Worker/Properties");
            builder.Services.AddPropertiesApplication();
            builder.AddPropertiesPersistence();
        }

        if (workerOptions.Modules.Inventory)
        {
            builder.SelectModuleProfile(InventoryProfiles.Default, "BunkFy.Host.Worker/Inventory");
            builder.Services.AddInventoryApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddInventoryTaskHandlers();
            }
            builder.AddInventoryPersistence();
        }

        if (workerOptions.Modules.Reservations)
        {
            builder.SelectModuleProfile(ReservationsProfiles.Default, "BunkFy.Host.Worker/Reservations");
            builder.Services.AddReservationsApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddReservationsTaskHandlers();
            }
            builder.AddReservationsPersistence();
        }

        if (workerOptions.Modules.Guests)
        {
            builder.SelectModuleProfile(GuestsProfiles.Default, "BunkFy.Host.Worker/Guests");
            builder.Services.AddGuestsApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddGuestsTaskHandlers();
            }
            builder.AddGuestsPersistence();
        }

        if (workerOptions.Modules.DataRights)
        {
            builder.SelectModuleProfile(
                DataRightsProfiles.Default,
                "BunkFy.Host.Worker/DataRights");
            builder.Services.AddDataRightsApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddDataRightsTaskHandlers();
            }
            builder.AddDataRightsPersistence();
            builder.AddDataRightsRestoreReadinessGate();
        }

        if (workerOptions.Modules.Staff)
        {
            builder.SelectModuleProfile(StaffProfiles.Default, "BunkFy.Host.Worker/Staff");
            builder.Services.AddStaffApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddStaffTaskHandlers();
            }
            builder.AddStaffPersistence();
        }

        if (workerOptions.Modules.Ingestion)
        {
            builder.AddMinioFileStorage();
            builder.SelectModuleProfile(IngestionProfiles.Default, "BunkFy.Host.Worker/Ingestion");
            builder.Services
                .AddOptions<AdapterIngressQuotaOptions>()
                .Bind(builder.Configuration.GetSection(AdapterIngressQuotaOptions.SectionName))
                .Validate(
                    AdapterIngressQuotaOptions.IsValid,
                    "Adapter ingress quotas must be positive, bounded, and tenant limits must cover credential limits.")
                .ValidateOnStart();
            builder.Services.AddIngestionApplication();
            builder.Services.AddLocalAdapterConfigurationMaterials(builder.Configuration);
            builder.Services.AddFakeHttpAdapter();
            builder.Services.AddImapReservationMailAdapter();
            builder.Services.AddReservationMailParser();
            builder.Services.AddJsonFileDropAdapter(ResolveJsonFileDropOptions(builder));
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddIngestionTaskHandlers();
            }
            builder.AddIngestionPersistence();
            if (workerOptions.Modules.Notifications &&
                workerOptions.Modules.Staff &&
                workerOptions.Modules.Organizations)
            {
                builder.Services
                    .AddBunkFyOperationsIngestionNotifications();
            }
        }

        if (workerOptions.Modules.Retention)
        {
            builder.SelectModuleProfile(
                RetentionProfiles.Default,
                "BunkFy.Host.Worker/Retention");
            builder.Services.AddRetentionApplication();
            if (workerOptions.TaskWorkerEnabled)
            {
                builder.Services.AddRetentionTaskHandlers();
            }
            if (workerOptions.Modules.TaskRuntime)
            {
                builder.Services.AddBunkFyRetentionTaskRuntimeRecovery();
            }
            builder.AddRetentionPersistence();
        }

        if (workerOptions.Modules.TaskRuntime)
        {
            builder.SelectModuleProfile(TaskRuntimeProfiles.Default, "BunkFy.Host.Worker/TaskRuntime");
            builder.Services.AddTaskRuntimeApplication();
            builder.AddTaskRuntimePersistence();
        }

        if (HasCompleteTenantTerminationOwnerTopology(workerOptions.Modules))
        {
            builder.Services.AddBunkFyAccessControlDataRights();
            builder.Services.AddBunkFyOrganizationsDataRights();
            builder.Services.AddBunkFyTaskRuntimeDataRights();
        }

    }

    private static bool HasCompleteTenantTerminationOwnerTopology(
        WorkerModuleOptions modules) =>
        modules.AccessControl &&
        modules.Auth &&
        modules.Notifications &&
        modules.Organizations &&
        modules.Properties &&
        modules.Inventory &&
        modules.Reservations &&
        modules.Guests &&
        modules.DataRights &&
        modules.Staff &&
        modules.Ingestion &&
        modules.Retention &&
        modules.TaskRuntime;

    private static bool RequiresWorkspaceTerminationAdmission(
        WorkerHostOptions options) =>
        options.TaskWorkerEnabled ||
        options.Modules.Properties ||
        options.Modules.Inventory ||
        options.Modules.Reservations ||
        options.Modules.Guests ||
        options.Modules.Staff ||
        options.Modules.Ingestion ||
        options.Modules.Retention;

    private static JsonFileDropAdapterOptions ResolveJsonFileDropOptions(IHostApplicationBuilder builder)
    {
        string configured = builder.Configuration["Adapters:JsonFileDrop:RootPath"] ??
            Path.Combine("data", "adapter-file-drop");
        string root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(builder.Environment.ContentRootPath, configured);
        return new JsonFileDropAdapterOptions(
            root,
            builder.Configuration.GetValue(
                "Adapters:JsonFileDrop:ProcessedArchiveRetention",
                JsonFileDropAdapterOptions.DefaultProcessedArchiveRetention),
            builder.Configuration.GetValue(
                "Adapters:JsonFileDrop:FailedQuarantineRetention",
                JsonFileDropAdapterOptions.DefaultFailedQuarantineRetention),
            builder.Configuration.GetValue(
                "Adapters:JsonFileDrop:MaximumDeletesPerRun",
                JsonFileDropAdapterOptions.DefaultMaximumDeletesPerRun),
            builder.Configuration.GetValue("Adapters:JsonFileDrop:RetentionEnabled", true));
    }

    private static bool HasRedisRateLimitingConnection(IHostApplicationBuilder builder)
    {
        RedisRateLimitingOptions options = builder.Configuration
            .GetSection(RedisRateLimitingOptions.SectionName)
            .Get<RedisRateLimitingOptions>() ?? new RedisRateLimitingOptions();
        return !string.IsNullOrWhiteSpace(
            builder.Configuration.GetConnectionString(options.ConnectionName));
    }
}
