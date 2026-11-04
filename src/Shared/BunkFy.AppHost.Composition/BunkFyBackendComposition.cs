namespace BunkFy.AppHost.Composition;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Diagnostics;

public static class BunkFyBackendComposition
{
    private const string MinioAccessKey = "minioadmin";
    private const string MinioSecretKey = "minioadmin";
    private const string FilesBucketName = "bunkfy-files";

    public static BunkFyBackendResources AddBunkFyBackend(
        this IDistributedApplicationBuilder builder,
        BunkFyBackendProjectPaths projectPaths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        projectPaths = projectPaths?.Validate() ?? throw new ArgumentNullException(nameof(projectPaths));

        IResourceBuilder<ParameterResource> postgreSqlPassword = builder.AddParameter(
            "postgres-password",
            "postgres",
            secret: true);
        IResourceBuilder<PostgresDatabaseResource> postgreSql = builder
            .AddPostgres("postgres", password: postgreSqlPassword)
            .WithDataVolume(isReadOnly: false)
            .AddDatabase("PostgreSql");

        bool sqlServerEnabled = IsEnabled(builder, "AppHost:SqlServer:Enabled");
        IResourceBuilder<SqlServerDatabaseResource>? sqlServer = sqlServerEnabled
            ? builder.AddSqlServer("sql").AddDatabase("SqlServer")
            : null;

        IResourceBuilder<NatsServerResource> nats = builder.AddNats("nats")
            .WithJetStream()
            .WithDataVolume(isReadOnly: false);

        string? storageProvider = builder.Configuration["AppHost:Storage:Provider"];
        bool useSilo = string.Equals(storageProvider, "Silo", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(storageProvider) && !useSilo &&
            !string.Equals(storageProvider, "Minio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AppHost:Storage:Provider must be Minio or Silo.");
        }

        string storageAccessKey = useSilo ? "bunkfy-development" : MinioAccessKey;
        IResourceBuilder<ParameterResource> storagePassword = useSilo
            ? builder.AddParameter("silo-password", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true)
            : builder.AddParameter("minio-password", MinioSecretKey, secret: true);
        IResourceBuilder<ContainerResource> minio = useSilo
            ? builder.AddContainer("minio", "pgsty/silo", "RELEASE.2026-10-26T00-00-00Z")
                .WithImageSHA256("7bb2aff6b89efe1cb1303c10fc21d579447434ed06649ac9ec05a5e2dbf30ad7")
                .WithContainerRuntimeArgs("--platform=linux/amd64")
            : builder.AddContainer("minio", "quay.io/minio/minio", "latest");
        minio
            .WithEnvironment("MINIO_ROOT_USER", storageAccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", storagePassword)
            .WithArgs("server", "/data", "--console-address", ":9001")
            .WithHttpEndpoint(targetPort: 9000, name: "api")
            .WithHttpEndpoint(targetPort: 9001, name: "console")
            .WithHttpHealthCheck("/minio/health/ready", endpointName: "api")
            .WithVolume(useSilo ? "bunkfy-silo-data" : "bunkfy-minio-data", "/data");
        if (useSilo && builder.ExecutionContext.IsRunMode)
        {
            builder.Eventing.Subscribe<BeforeStartEvent>((_, cancellationToken) =>
                AssertNoLegacyStorageVolumeAsync(cancellationToken));
        }

        IResourceBuilder<ProjectResource> migrations = builder
            .AddProject("bunkfy-host-migrations", projectPaths.Migrations)
            .WithReference(postgreSql)
            .WaitFor(postgreSql);

        bool workerEnabled = IsEnabled(builder, "AppHost:Worker:Enabled");
        IResourceBuilder<ProjectResource> api = builder
            .AddProject("bunkfy-host-api", projectPaths.Api)
            .WithReference(postgreSql)
            .WithReference(nats)
            .WaitFor(postgreSql)
            .WaitForCompletion(migrations)
            .WaitFor(nats)
            .WaitFor(minio)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithMinioFileStorage(minio, storageAccessKey, storagePassword)
            .WithEnvironment("Notifications__Enabled", "true")
            .WithEnvironment("NatsJetStream__Enabled", workerEnabled ? "false" : "true")
            .WithHttpHealthCheck("/health");

        if (sqlServer is { } configuredSqlServer)
        {
            api.WithReference(configuredSqlServer).WaitFor(configuredSqlServer);
        }

        IResourceBuilder<ProjectResource>? adminApi = AddAdminApi(
            builder,
            projectPaths.AdminApi,
            workerEnabled,
            postgreSql,
            nats,
            minio,
            storageAccessKey,
            storagePassword,
            migrations,
            sqlServer);
        IResourceBuilder<ProjectResource>? worker = AddWorker(
            builder,
            projectPaths.Worker,
            workerEnabled,
            postgreSql,
            nats,
            minio,
            storageAccessKey,
            storagePassword,
            migrations,
            sqlServer);

        if (IsEnabled(builder, "AppHost:Redis:Enabled"))
        {
            IResourceBuilder<RedisResource> redis = builder.AddRedis("redis");
            api.WithReference(redis).WaitFor(redis).WithRedisCaching();
            adminApi?.WithReference(redis).WaitFor(redis).WithRedisCaching();
            worker?.WithReference(redis).WaitFor(redis).WithRedisCaching();
        }

        return new BunkFyBackendResources(api);
    }

    private static IResourceBuilder<ProjectResource>? AddAdminApi(
        IDistributedApplicationBuilder builder,
        string projectPath,
        bool workerEnabled,
        IResourceBuilder<PostgresDatabaseResource> postgreSql,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ContainerResource> minio,
        string storageAccessKey,
        IResourceBuilder<ParameterResource> storagePassword,
        IResourceBuilder<ProjectResource> migrations,
        IResourceBuilder<SqlServerDatabaseResource>? sqlServer)
    {
        if (!IsEnabled(builder, "AppHost:AdminApi:Enabled"))
        {
            return null;
        }

        IResourceBuilder<ProjectResource> adminApi = builder
            .AddProject("bunkfy-host-admin-api", projectPath)
            .WithReference(postgreSql)
            .WithReference(nats)
            .WaitFor(postgreSql)
            .WaitForCompletion(migrations)
            .WaitFor(nats)
            .WaitFor(minio)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithMinioFileStorage(minio, storageAccessKey, storagePassword)
            .WithEnvironment("NatsJetStream__Enabled", workerEnabled ? "false" : "true")
            .WithHttpHealthCheck("/health");
        if (sqlServer is { } configuredSqlServer)
        {
            adminApi.WithReference(configuredSqlServer).WaitFor(configuredSqlServer);
        }

        return adminApi;
    }

    private static IResourceBuilder<ProjectResource>? AddWorker(
        IDistributedApplicationBuilder builder,
        string projectPath,
        bool workerEnabled,
        IResourceBuilder<PostgresDatabaseResource> postgreSql,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ContainerResource> minio,
        string storageAccessKey,
        IResourceBuilder<ParameterResource> storagePassword,
        IResourceBuilder<ProjectResource> migrations,
        IResourceBuilder<SqlServerDatabaseResource>? sqlServer)
    {
        if (!workerEnabled)
        {
            return null;
        }

        IResourceBuilder<ProjectResource> worker = builder
            .AddProject("bunkfy-host-worker", projectPath)
            .WithReference(postgreSql)
            .WithReference(nats)
            .WaitFor(postgreSql)
            .WaitForCompletion(migrations)
            .WaitFor(nats)
            .WaitFor(minio)
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithMinioFileStorage(minio, storageAccessKey, storagePassword)
            .WithEnvironment("NatsJetStream__Enabled", "true")
            .WithEnvironment("NatsConsumers__Enabled", "true")
            .WithEnvironment("Tasks__Worker__Enabled", "true")
            .WithEnvironment("Tasks__Scheduler__Enabled", "true")
            .WithEnvironment("Worker__Modules__AccessControl", "true")
            .WithEnvironment("Worker__Modules__Auth", "true")
            .WithEnvironment("Auth__Retention__Enabled", "true")
            .WithEnvironment("Worker__Modules__Notifications", "true")
            .WithEnvironment("Worker__Modules__Organizations", "true")
            .WithEnvironment("Organizations__Lifecycle__Enabled", "true")
            .WithEnvironment("Organizations__Retention__Enabled", "true")
            .WithEnvironment("Worker__Modules__Properties", "true")
            .WithEnvironment("Worker__Modules__Inventory", "true")
            .WithEnvironment("Worker__Modules__Reservations", "true")
            .WithEnvironment("Worker__Modules__Guests", "true")
            .WithEnvironment("Worker__Modules__DataRights", "true")
            .WithEnvironment("Worker__Modules__Staff", "true")
            .WithEnvironment("Worker__Modules__Ingestion", "true")
            .WithEnvironment("Worker__Modules__Retention", "true")
            .WithEnvironment("Worker__Modules__TaskRuntime", "true")
            .WithEnvironment("Tasks__Worker__WorkerGroups__0", "default")
            .WithEnvironment("Tasks__Worker__WorkerGroups__1", "projection-workers")
            .WithEnvironment("Tasks__Worker__WorkerGroups__2", "reminder-workers")
            .WithEnvironment("Tasks__Worker__WorkerGroups__3", "ingestion-adapters")
            .WithEnvironment("Tasks__Worker__WorkerGroups__4", "ingestion-maintenance")
            .WithEnvironment("Tasks__Worker__WorkerGroups__5", "data-rights-workers")
            .WithEnvironment("Tasks__Worker__WorkerGroups__6", "retention-workers")
            .WithEnvironment("Tasks__Worker__WorkerGroups__7", "tenant-termination-workers")
            .WithEnvironment("Tasks__Worker__WorkerGroups__8", "guest-record-link-workers");
        if (sqlServer is { } configuredSqlServer)
        {
            worker.WithReference(configuredSqlServer).WaitFor(configuredSqlServer);
        }

        return worker;
    }

    private static IResourceBuilder<T> WithMinioFileStorage<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<ContainerResource> minio,
        string storageAccessKey,
        IResourceBuilder<ParameterResource> storagePassword)
        where T : IResourceWithEnvironment => resource
        .WithEnvironment("FileManagement__Provider", "Minio")
        .WithEnvironment("FileManagement__Minio__Endpoint", minio.GetEndpoint("api").Property(EndpointProperty.HostAndPort))
        .WithEnvironment("FileManagement__Minio__AccessKey", storageAccessKey)
        .WithEnvironment("FileManagement__Minio__SecretKey", storagePassword)
        .WithEnvironment("FileManagement__Minio__BucketName", FilesBucketName)
        .WithEnvironment("FileManagement__Minio__UseSsl", "false")
        .WithEnvironment("FileManagement__Minio__CreateBucketIfMissing", "true");

    private static IResourceBuilder<T> WithRedisCaching<T>(this IResourceBuilder<T> resource)
        where T : IResourceWithEnvironment => resource
        .WithEnvironment("Caching__Enabled", "true")
        .WithEnvironment("Caching__Provider", "Redis");

    private static async Task AssertNoLegacyStorageVolumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Choosing the new provider is explicit and never mounts the old data.
        // Stop before any resources/migrations start if this host has legacy
        // storage; its existing environment needs separate migration review.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (string argument in new[] { "volume", "ls", "--filter", "name=^bunkfy-minio-data$", "--format", "{{.Name}}" })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Cannot inspect the existing development storage volume.");
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        if (process.ExitCode != 0 || output.Result.Length > 256)
        {
            throw new InvalidOperationException("Cannot verify existing development storage; no resources were started.");
        }
        if (!string.IsNullOrWhiteSpace(output.Result))
        {
            throw new InvalidOperationException("Existing bunkfy-minio-data requires the original Minio profile. The Silo profile is for a fresh environment; cross-provider migration is not automatic.");
        }
    }

    private static bool IsEnabled(IDistributedApplicationBuilder builder, string key) =>
        bool.TryParse(builder.Configuration[key], out bool enabled) && enabled;
}
