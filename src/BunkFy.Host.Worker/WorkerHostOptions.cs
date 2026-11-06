namespace BunkFy.Host.Worker;

using Microsoft.Extensions.Configuration;

public sealed class WorkerHostOptions
{
    public const string SectionName = "Worker";

    private WorkerHostOptions(
        WorkerModuleOptions modules,
        bool natsPublishingEnabled,
        bool natsConsumersEnabled,
        bool taskWorkerEnabled)
    {
        this.Modules = modules;
        this.NatsPublishingEnabled = natsPublishingEnabled;
        this.NatsConsumersEnabled = natsConsumersEnabled;
        this.TaskWorkerEnabled = taskWorkerEnabled;
    }

    public WorkerModuleOptions Modules { get; }
    public bool NatsPublishingEnabled { get; }
    public bool NatsConsumersEnabled { get; }
    public bool TaskWorkerEnabled { get; }

    public static WorkerHostOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection modules = configuration.GetSection($"{SectionName}:Modules");
        WorkerModuleOptions moduleOptions = new(
            GetBoolean(modules, nameof(WorkerModuleOptions.AccessControl), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Auth), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Notifications), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Organizations), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Properties), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Inventory), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Reservations), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Guests), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.DataRights), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Staff), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Ingestion), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.Retention), defaultValue: false),
            GetBoolean(modules, nameof(WorkerModuleOptions.TaskRuntime), defaultValue: false));

        WorkerHostOptions options = new(
            moduleOptions,
            GetBoolean(configuration, "NatsJetStream:Enabled", defaultValue: false),
            GetBoolean(configuration, "NatsConsumers:Enabled", defaultValue: false),
            GetBoolean(configuration, "Tasks:Worker:Enabled", defaultValue: false));
        options.ValidateRecoveryTopology();
        return options;
    }

    public IReadOnlyList<string> GetComposedModuleNames()
    {
        List<string> modules = [];

        if (this.Modules.AccessControl)
        {
            modules.Add("access-control");
        }

        if (this.Modules.Auth)
        {
            modules.Add("auth");
        }

        if (this.Modules.Notifications)
        {
            modules.Add("notifications");
        }

        if (this.Modules.Organizations)
        {
            modules.Add("organizations");
        }

        if (this.Modules.Properties)
        {
            modules.Add("properties");
        }

        if (this.Modules.Inventory)
        {
            modules.Add("inventory");
        }

        if (this.Modules.Reservations)
        {
            modules.Add("reservations");
        }

        if (this.Modules.Guests)
        {
            modules.Add("guests");
        }

        if (this.Modules.DataRights)
        {
            modules.Add("data-rights");
        }

        if (this.Modules.Staff)
        {
            modules.Add("staff");
        }

        if (this.Modules.Ingestion)
        {
            modules.Add("ingestion");
        }

        if (this.Modules.Retention)
        {
            modules.Add("retention");
        }

        if (this.Modules.TaskRuntime)
        {
            modules.Add("task-runtime");
        }

        return modules;
    }

    private static bool GetBoolean(IConfiguration configuration, string key, bool defaultValue)
    {
        string? value = configuration[key];
        return bool.TryParse(value, out bool parsed)
            ? parsed
            : defaultValue;
    }

    private void ValidateRecoveryTopology()
    {
        if (this.NatsConsumersEnabled &&
            this.Modules.Retention &&
            !this.Modules.TaskRuntime)
        {
            throw new InvalidOperationException(
                "A Worker that consumes Retention messages must compose TaskRuntime for durable recovery execution.");
        }
    }
}

public sealed record WorkerModuleOptions(
    bool AccessControl,
    bool Auth,
    bool Notifications,
    bool Organizations,
    bool Properties,
    bool Inventory,
    bool Reservations,
    bool Guests,
    bool DataRights,
    bool Staff,
    bool Ingestion,
    bool Retention,
    bool TaskRuntime);
