namespace Integration.Tests;

using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using BunkFy.Adapter.Abstractions;
using BunkFy.Adapters.ImapReservationMail;
using BunkFy.Adapters.JsonFileDrop;
using BunkFy.Extensions.DataRights.TenantTermination;
using BunkFy.Host.Worker;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using BunkFy.Modules.Ingestion.Domain.Runs;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.ObservationParsing;
using BunkFy.Parsers.ReservationMail;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Nats;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Domain.Services;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.Contracts;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Testcontainers.PostgreSql;
using Xunit;
using IOrganizationAccessCandidateFilter =
    Gma.Modules.Organizations.Contracts.IOrganizationAccessCandidateFilter;

public sealed class WorkerHostIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Retention_consumer_requires_local_task_runtime_recovery()
    {
        ConfigurationManager configuration = new();
        configuration["NatsConsumers:Enabled"] = "true";
        configuration["Worker:Modules:Retention"] = "true";
        configuration["Worker:Modules:TaskRuntime"] = "false";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WorkerHostOptions.FromConfiguration(configuration));

        Assert.Contains("must compose TaskRuntime", error.Message);

        configuration["NatsConsumers:Enabled"] = "false";
        WorkerHostOptions apiOnly = WorkerHostOptions.FromConfiguration(
            configuration);
        Assert.True(apiOnly.Modules.Retention);
        Assert.False(apiOnly.Modules.TaskRuntime);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Worker_host_composes_the_exact_tenant_termination_catalogue()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "true";
        builder.Configuration["Tasks:Worker:WorkerGroups:0"] =
            DataRightsModuleMetadata.TenantTerminationWorkerGroup;
        builder.Configuration["Tasks:Worker:TimeoutScannerEnabled"] = "false";
        builder.Configuration["Tasks:Worker:MetricsSamplerEnabled"] = "false";
        builder.Configuration[
            $"{TenantTerminationProductionAdmissionOptions.SectionName}:ExecutionEnabled"] =
            "false";
        foreach (string module in new[]
                 {
                     "AccessControl",
                     "Auth",
                     "Notifications",
                     "Organizations",
                     "Properties",
                     "Inventory",
                     "Reservations",
                     "Guests",
                     "DataRights",
                     "Staff",
                     "Ingestion",
                     "Retention",
                     "TaskRuntime"
                 })
        {
            builder.Configuration[$"Worker:Modules:{module}"] = "true";
        }

        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "Minio";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] =
            "application/json";
        builder.Configuration["FileManagement:Minio:Endpoint"] =
            "localhost:9000";
        builder.Configuration["FileManagement:Minio:AccessKey"] = "test";
        builder.Configuration["FileManagement:Minio:SecretKey"] =
            "test-secret";
        builder.Configuration["FileManagement:Minio:BucketName"] = "test";
        builder.Configuration["Notifications:Adapters:Email:Enabled"] =
            "false";
        AuthTestConfiguration.ConfigureTokenHashing(builder.Configuration);
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult composition =
            builder.ValidateModuleComposition();
        using IHost worker = builder.Build();
        using IServiceScope scope = worker.Services.CreateScope();
        ITenantTerminationProductionCatalog catalog = scope.ServiceProvider
            .GetRequiredService<ITenantTerminationProductionCatalog>();
        Result<TenantTerminationProductionCatalogEvidence> evidence =
            catalog.Validate(
                TenantTerminationProductionOwnerCatalog.RequiredOwnerKeys);
        ITenantTerminationContributor guests = scope.ServiceProvider
            .GetServices<ITenantTerminationContributor>()
            .Single(contributor => contributor.Descriptor.OwnerKey ==
                GuestsTenantTerminationMetadata.OwnerKey);

        Assert.True(composition.IsValid, composition.Report);
        Assert.True(evidence.IsSuccess);
        Assert.Equal(12, evidence.Value.OwnerCount);
        Assert.Equal("workspaces", evidence.Value.TerminalOwnerKey);
        Assert.Matches("^[0-9a-f]{64}$", evidence.Value.CatalogSha256);
        Assert.Equal(6, guests.Descriptor.CatalogVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.CatalogSha256,
            guests.Descriptor.CatalogSha256);
        Assert.Contains(
            "catalog=6",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catalog=5",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        await AssertOldGuestsCatalogCoordinateRejectedAsync(
            scope.ServiceProvider);
    }

    private static async Task AssertOldGuestsCatalogCoordinateRejectedAsync(
        IServiceProvider services)
    {
        ITenantTerminationContributor[] contributors = services
            .GetServices<ITenantTerminationContributor>()
            .ToArray();
        TenantTerminationContributorDescriptor liveGuests = contributors
            .Single(contributor => contributor.Descriptor.OwnerKey ==
                GuestsTenantTerminationMetadata.OwnerKey)
            .Descriptor;
        string version5Manifest = GuestsTenantTerminationMetadata
            .CatalogManifest
            .Replace("|catalog=6|", "|catalog=5|", StringComparison.Ordinal)
            .Replace(
                "|personal-data-catalog=17|",
                "|personal-data-catalog=16|",
                StringComparison.Ordinal)
            .Replace(
                "|export-schema=guests.tenant-termination-export:5|",
                "|export-schema=guests.tenant-termination-export:4|",
                StringComparison.Ordinal);
        string version5Sha256 = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(version5Manifest)));
        TenantTerminationExportOwnerCatalogEntry[] frozenOwners = contributors
            .Where(contributor => contributor.Descriptor.PhasePlans.Any(
                plan => plan.Phase ==
                    TenantTerminationContributionPhase.Export))
            .Select(contributor => contributor.Descriptor)
            .Select(descriptor => new TenantTerminationExportOwnerCatalogEntry(
                descriptor.OwnerKey,
                descriptor.ContractVersion,
                descriptor.OwnerKey ==
                    GuestsTenantTerminationMetadata.OwnerKey
                    ? 5
                    : descriptor.CatalogVersion,
                descriptor.OwnerKey ==
                    GuestsTenantTerminationMetadata.OwnerKey
                    ? version5Sha256
                    : descriptor.CatalogSha256))
            .ToArray();
        TenantTerminationExportOwnerCatalogEntry oldGuests = frozenOwners
            .Single(owner => owner.OwnerKey ==
                GuestsTenantTerminationMetadata.OwnerKey);
        DateTimeOffset frozenAtUtc = new(
            2026,
            8,
            15,
            12,
            0,
            0,
            TimeSpan.Zero);
        var frozenRevision = new TenantTerminationFrozenRevision(
            "7f000000-0000-0000-0000-000000000001",
            Guid.Parse("7f100000-0000-0000-0000-000000000001"),
            Guid.Parse("7f200000-0000-0000-0000-000000000001"),
            ApprovalRevision: 1,
            FreezeOperationRevision: 2,
            Guid.Parse("7f300000-0000-0000-0000-000000000001"),
            WorkspaceFenceRevision: 3,
            new string('a', TenantTerminationContract.Sha256Length),
            frozenAtUtc,
            frozenOwners);
        ITenantTerminationExportFragmentAssembler assembler = services
            .GetRequiredService<ITenantTerminationExportFragmentAssembler>();
        string frozenRevisionSha256 = Assert.IsType<string>(assembler
            .GetType()
            .GetMethod(
                "ComputeFrozenRevisionSha256",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic,
                binder: null,
                [typeof(TenantTerminationFrozenRevision)],
                modifiers: null)!
            .Invoke(null, [frozenRevision]));
        var request = new TenantTerminationExportFragmentAssemblyRequest(
            frozenRevision.TenantId,
            frozenRevision.ProcessId,
            frozenRevision.CaseId,
            frozenRevision.ApprovalRevision,
            frozenRevision.FreezeOperationRevision,
            ExportOperationRevision: 3,
            frozenRevision.TerminationEpoch,
            frozenRevision.WorkspaceFenceRevision,
            frozenRevisionSha256,
            frozenRevision.PolicyEvidenceSha256,
            "integration:catalog-v4-replay",
            frozenRevision.FrozenAtUtc,
            frozenRevision.FrozenAtUtc.AddMinutes(1),
            frozenRevision.FrozenAtUtc.AddMinutes(10),
            new TenantTerminationExportOwnerWork(
                oldGuests.OwnerKey,
                Guid.Parse("7f400000-0000-0000-0000-000000000001"),
                Guid.Parse("7f500000-0000-0000-0000-000000000001"),
                oldGuests.ContractVersion,
                oldGuests.CatalogVersion,
                oldGuests.CatalogSha256),
            frozenOwners);
        await using MemoryStream output = new();

        DataRightsExportGenerationException rejected =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    request,
                    output,
                    CancellationToken.None));

        Assert.Equal(6, liveGuests.CatalogVersion);
        Assert.Equal(5, oldGuests.CatalogVersion);
        Assert.NotEqual(liveGuests.CatalogSha256, oldGuests.CatalogSha256);
        Assert.Equal("tenant-export-owner-catalog-invalid", rejected.Code);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Worker_host_composes_properties_and_inventory_projection_services()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "false";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Inventory"] = "true";
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        using IHost worker = builder.Build();
        using IServiceScope scope = worker.Services.CreateScope();

        Assert.True(result.IsValid, result.Report);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PropertiesDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<InventoryDbContext>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IWorkspaceTerminationFenceReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IInventoryAvailabilityProjectionExportSource>());
        Assert.Contains(
            worker.Services.GetRequiredService<IIntegrationEventSubscriptionRegistry>().Subscriptions,
            subscription => subscription.ConsumerModule == InventoryModuleMetadata.Name);
        Assert.Contains(
            worker.Services.GetRequiredService<IIntegrationEventSubscriptionRegistry>().Subscriptions,
            subscription =>
                subscription.ConsumerModule == PropertiesModuleMetadata.Name &&
                subscription.HandlerName ==
                    PropertiesModuleMetadata.BedRetirementFinalizationHandlerName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Worker_host_composes_auth_and_product_notification_bridges()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "false";
        builder.Configuration["Worker:Modules:Auth"] = "true";
        builder.Configuration["Worker:Modules:Notifications"] = "true";
        builder.Configuration["Worker:Modules:Organizations"] = "true";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Inventory"] = "true";
        builder.Configuration["Worker:Modules:Reservations"] = "true";
        builder.Configuration["Worker:Modules:Guests"] = "true";
        builder.Configuration["Worker:Modules:Staff"] = "true";
        AuthTestConfiguration.ConfigureTokenHashing(builder.Configuration);
        builder.Configuration["Notifications:Adapters:Email:Enabled"] = "false";
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        using IHost worker = builder.Build();
        using IServiceScope scope = worker.Services.CreateScope();
        IIntegrationEventSubscriptionRegistry subscriptions =
            worker.Services.GetRequiredService<IIntegrationEventSubscriptionRegistry>();

        Assert.True(result.IsValid, result.Report);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRefreshTokenHashingService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUserNotificationRequestProjector>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOrganizationAccessCandidateFilter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IStaffPropertyAudienceReader>());
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription => subscription.HandlerName == "auth-member-authenticated-notification");
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription => subscription.HandlerName == "bunkfy-reservation-confirmed-notification");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Worker_host_composes_organization_expiry_and_workspaces_consumers_once()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "false";
        builder.Configuration["Worker:Modules:AccessControl"] = "true";
        builder.Configuration["Worker:Modules:Auth"] = "true";
        builder.Configuration["Worker:Modules:Organizations"] = "true";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Staff"] = "true";
        builder.Configuration["Organizations:Lifecycle:Enabled"] = "true";
        AuthTestConfiguration.ConfigureTokenHashing(builder.Configuration);
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        using IHost worker = builder.Build();
        using IServiceScope scope = worker.Services.CreateScope();
        IIntegrationEventSubscriptionRegistry subscriptions =
            worker.Services.GetRequiredService<IIntegrationEventSubscriptionRegistry>();

        Assert.True(result.IsValid, result.Report);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IStaffOnboardingStateVerifier>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IAccessControlSubjectAuthoritySnapshotReader>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IScopedAccessProfileManager>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IWorkspaceStaffOnboardingRecoveryOperationLock>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<IWorkspaceStaffOnboardingRecoveryReceiptRepository>());
        Assert.Single(
            worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "OrganizationsLifecycleService");
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName == WorkspacesModuleMetadata.InvitationExpiredHandlerName);
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName == WorkspacesModuleMetadata.EnrollmentClaimExpiredHandlerName);
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName ==
                    WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName);
        Assert.Contains(
            subscriptions.Subscriptions,
            subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName == WorkspacesModuleMetadata.EnrollmentLinkExpiredHandlerName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Worker_host_registers_selected_module_task_handlers_when_task_execution_is_enabled()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "true";
        builder.Configuration["Tasks:Worker:TimeoutScannerEnabled"] = "false";
        builder.Configuration["Tasks:Worker:MetricsSamplerEnabled"] = "false";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Inventory"] = "true";
        builder.Configuration["Worker:Modules:Reservations"] = "true";
        builder.Configuration["Worker:Modules:Guests"] = "true";
        builder.Configuration["Worker:Modules:Ingestion"] = "true";
        builder.Configuration["Worker:Modules:Organizations"] = "true";
        builder.Configuration["Worker:Modules:Retention"] = "true";
        builder.Configuration["Worker:Modules:TaskRuntime"] = "true";
        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "Minio";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] = "application/json";
        builder.Configuration["FileManagement:Minio:Endpoint"] = "localhost:9000";
        builder.Configuration["FileManagement:Minio:AccessKey"] = "test";
        builder.Configuration["FileManagement:Minio:SecretKey"] = "test-secret";
        builder.Configuration["FileManagement:Minio:BucketName"] = "test";
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        using IHost worker = builder.Build();
        using IServiceScope scope = worker.Services.CreateScope();
        ITaskHandlerRegistry registry = worker.Services.GetRequiredService<ITaskHandlerRegistry>();

        Assert.True(result.IsValid, result.Report);
        Assert.NotNull(registry.Find(
            InventoryModuleMetadata.Name,
            RebuildInventoryTopologyPayload.TaskName,
            RebuildInventoryTopologyPayload.PayloadVersion));
        Assert.NotNull(registry.Find(
            ReservationsModuleMetadata.Name,
            RebuildReservationInventoryProjectionPayload.TaskName,
            RebuildReservationInventoryProjectionPayload.PayloadVersion));
        Assert.NotNull(registry.Find(
            ReservationsModuleMetadata.Name,
            RebuildReservationGuestRestrictionsPayload.TaskName,
            RebuildReservationGuestRestrictionsPayload.PayloadVersion));
        Assert.NotNull(registry.Find(
            IngestionModuleMetadata.Name,
            RunAdapterTaskPayload.TaskName,
            RunAdapterTaskPayload.PayloadVersion));
        Assert.NotNull(registry.Find(
            IngestionModuleMetadata.Name,
            ReprocessObservationPayload.TaskName,
            ReprocessObservationPayload.PayloadVersion));
        Assert.NotNull(registry.Find(
            RetentionModuleMetadata.Name,
            ExecuteRetentionSchedulePayload.TaskName,
            ExecuteRetentionSchedulePayload.PayloadVersion));
        Assert.Contains(
            scope.ServiceProvider.GetServices<ITaskScheduleProvider>(),
            provider => provider.GetType().Name == "IngestionPollingScheduleProvider");
        Assert.Contains(
            scope.ServiceProvider.GetServices<ITaskScheduleProvider>(),
            provider => provider.GetType().Name == "RetentionScheduleProvider");
        Assert.Contains(
            scope.ServiceProvider.GetServices<IAdapterDescriptorProvider>(),
            provider => provider.Descriptor.AdapterType == JsonFileDropAdapterDescriptor.AdapterType);
        Assert.Contains(
            scope.ServiceProvider.GetServices<IAdapterRunner>(),
            runner => runner.Descriptor.AdapterType == JsonFileDropAdapterDescriptor.AdapterType);
        Assert.Contains(
            scope.ServiceProvider.GetServices<IAdapterDescriptorProvider>(),
            provider => provider.Descriptor.AdapterType == ImapReservationMailAdapterDescriptor.AdapterType);
        Assert.Contains(
            scope.ServiceProvider.GetServices<IAdapterRunner>(),
            runner => runner.Descriptor.AdapterType == ImapReservationMailAdapterDescriptor.AdapterType);
        Assert.Contains(
            scope.ServiceProvider.GetServices<IObservationParserDescriptorProvider>(),
            provider => provider.Descriptor.ParserType == ReservationMailParserDescriptor.ParserType);
        Assert.Contains(
            scope.ServiceProvider.GetServices<IObservationParser>(),
            parser => parser.Descriptor.ParserType == ReservationMailParserDescriptor.ParserType);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Worker_host_processes_json_file_drop_through_task_runtime()
    {
        const string tenantId = "tenant-file-drop-worker";
        Guid propertyId = Guid.Parse("41000000-0000-0000-0000-000000000001");
        Guid connectionId = Guid.Parse("41000000-0000-0000-0000-000000000002");
        Guid taskRunId = Guid.Parse("41000000-0000-0000-0000-000000000003");
        Guid mailConnectionId = Guid.Parse("41000000-0000-0000-0000-000000000004");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-file-drop-worker-{Guid.NewGuid():N}");
        string pending = Path.Combine(root, connectionId.ToString("N"), "pending");
        Directory.CreateDirectory(pending);
        string processed = Path.Combine(root, connectionId.ToString("N"), "processed");
        string failed = Path.Combine(root, connectionId.ToString("N"), "failed");
        Directory.CreateDirectory(processed);
        Directory.CreateDirectory(failed);
        string expiredProcessed = Path.Combine(processed, "expired-processed.json");
        string expiredFailed = Path.Combine(failed, "expired-failed.json");
        string expiredFailedMetadata = expiredFailed + ".failure.json";
        await File.WriteAllTextAsync(expiredProcessed, "expired processed artifact").ConfigureAwait(false);
        await File.WriteAllTextAsync(expiredFailed, "expired failed artifact").ConfigureAwait(false);
        await File.WriteAllTextAsync(expiredFailedMetadata, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            originalFileName = "expired-failed.json",
            errorCode = "json-file-drop.invalid-json",
            quarantinedAtUtc = DateTimeOffset.UtcNow.AddHours(-2)
        })).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(expiredProcessed, DateTime.UtcNow.AddHours(-2));
        string fileName = "20260712T130000Z-worker.json";
        string invalidFileName = "20260712T125900Z-invalid.json";
        await File.WriteAllTextAsync(
            Path.Combine(pending, invalidFileName),
            "{ permanently invalid").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(pending, fileName), /*lang=json,strict*/ """
            {
              "schemaVersion": 1,
              "recordType": "operations.file-drop.v1",
              "externalRecordId": "worker-file-drop-1",
              "sourceRevision": "1",
              "sourceUpdatedAtUtc": "2026-07-12T12:59:00Z",
              "observedAtUtc": "2026-07-12T13:00:00Z",
              "payload": { "source": "worker-json-file-drop" }
            }
            """).ConfigureAwait(false);

        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_file_drop_worker_tests")
            .Build();
        await using IContainer minio = ProviderTestContainers.Minio()
            .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();
        await using IContainer redis = new ContainerBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();

        try
        {
            await Task.WhenAll(
                postgreSql.StartAsync(),
                minio.StartAsync(),
                redis.StartAsync()).ConfigureAwait(false);
            using IHost worker = BuildFileDropWorker(
                postgreSql.GetConnectionString(),
                $"127.0.0.1:{redis.GetMappedPublicPort(6379)},abortConnect=false",
                $"localhost:{minio.GetMappedPublicPort(9000)}",
                $"bunkfy-file-drop-worker-{Guid.NewGuid():N}",
                root);
            await MigrateFileDropWorkerStoresAsync(worker).ConfigureAwait(false);
            await SeedFileDropConnectionAsync(worker, tenantId, propertyId, connectionId).ConfigureAwait(false);
            await EnqueueFileDropTaskAsync(worker, tenantId, connectionId, taskRunId).ConfigureAwait(false);
            ObservationReprocessingPreparation reprocessing = await PrepareMailReprocessingAsync(
                worker,
                tenantId,
                propertyId,
                mailConnectionId).ConfigureAwait(false);
            await EnqueueReprocessingTaskAsync(worker, tenantId, reprocessing).ConfigureAwait(false);

            await worker.StartAsync().ConfigureAwait(false);
            try
            {
                TaskRunSnapshot snapshot = await WaitForStatusAsync(
                    worker,
                    taskRunId,
                    TaskRunStatus.Succeeded,
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Assert.Equal(TaskRunStatus.Succeeded, snapshot.Status);
                Assert.Null(snapshot.LastError);
                TaskRunSnapshot reprocessingTask = await WaitForStatusAsync(
                    worker,
                    reprocessing.TaskRunId,
                    TaskRunStatus.Succeeded,
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Assert.Equal(TaskRunStatus.Succeeded, reprocessingTask.Status);

                using IServiceScope scope = worker.Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
                IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
                Assert.Equal(
                    "worker-file-drop-1",
                    (await dbContext.ObservationReceipts.SingleAsync(
                        receipt => receipt.ConnectionId == connectionId).ConfigureAwait(false)).ExternalId);
                IngestionRun ingestionRun = await dbContext.Runs.SingleAsync(
                    run => run.ConnectionId == connectionId).ConfigureAwait(false);
                Assert.Equal(IngestionRunState.PartiallySucceeded, ingestionRun.State);
                Assert.Equal(1, ingestionRun.ObservedCount);
                Assert.Equal(1, ingestionRun.AcceptedCount);
                Assert.Equal("json-file-drop.input-quarantined", ingestionRun.ErrorCode);
                Assert.False(File.Exists(Path.Combine(pending, fileName)));
                Assert.False(File.Exists(Path.Combine(pending, invalidFileName)));
                Assert.True(File.Exists(Path.Combine(
                    root,
                    connectionId.ToString("N"),
                    "processed",
                    fileName)));
                Assert.True(File.Exists(Path.Combine(failed, invalidFileName)));
                Assert.Single(Directory.EnumerateFiles(failed, "*.failure.json"));
                Assert.False(File.Exists(expiredProcessed));
                Assert.False(File.Exists(expiredFailed));
                Assert.False(File.Exists(expiredFailedMetadata));

                ObservationReprocessingAttempt attempt = await dbContext.ObservationReprocessingAttempts
                    .SingleAsync(item => item.Id == reprocessing.AttemptId).ConfigureAwait(false);
                Assert.Equal(ObservationReprocessingState.Succeeded, attempt.State);
                Assert.Equal(1, attempt.AcceptedCount);
                ObservationReprocessingOutput output = await dbContext.ObservationReprocessingOutputs
                    .SingleAsync(item => item.AttemptId == attempt.Id).ConfigureAwait(false);
                Assert.Equal(ObservationReprocessingOutputDisposition.Accepted, output.Disposition);
                ObservationReceipt derived = await dbContext.ObservationReceipts
                    .SingleAsync(receipt => receipt.ReprocessingAttemptId == attempt.Id).ConfigureAwait(false);
                Assert.Equal("reservation.v1", derived.SourceRecordType);
                Assert.Equal(attempt.SourceReceiptId, derived.SourceReceiptId);
                Assert.Equal(output.ReceiptId, derived.Id);
                ObservationReceipt source = await dbContext.ObservationReceipts
                    .SingleAsync(receipt => receipt.Id == attempt.SourceReceiptId).ConfigureAwait(false);
                Assert.Equal(ObservationReceiptState.Rejected, source.State);
                Assert.Null(source.ActiveReprocessingAttemptId);
                AdapterConnection mailConnection = await dbContext.AdapterConnections
                    .SingleAsync(connection => connection.Id == mailConnectionId).ConfigureAwait(false);
                Assert.Equal(AdapterConnectionState.Disabled, mailConnection.State);
            }
            finally
            {
                await worker.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static IHost BuildFileDropWorker(
        string postgreSqlConnectionString,
        string redisConnectionString,
        string minioEndpoint,
        string minioBucket,
        string fileDropRoot)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = postgreSqlConnectionString;
        builder.Configuration["ConnectionStrings:redis"] = redisConnectionString;
        builder.Configuration["Tenancy:Enabled"] = "true";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Outbox:PollIntervalMilliseconds"] = "100";
        builder.Configuration["Tasks:Worker:Enabled"] = "true";
        builder.Configuration["Tasks:Worker:WorkerGroups:0"] = IngestionModuleMetadata.AdapterWorkerGroup;
        builder.Configuration["Tasks:Worker:WorkerGroups:1"] = IngestionModuleMetadata.MaintenanceWorkerGroup;
        builder.Configuration["Tasks:Worker:BatchSize"] = "1";
        builder.Configuration["Tasks:Worker:MaxConcurrency"] = "1";
        builder.Configuration["Tasks:Worker:PollInterval"] = "00:00:00.100";
        builder.Configuration["Tasks:Worker:LeaseDuration"] = "00:00:30";
        builder.Configuration["Tasks:Worker:HandlerTimeout"] = "00:00:30";
        builder.Configuration["Tasks:Worker:RetryBaseDelay"] = "00:00:00.100";
        builder.Configuration["Tasks:Worker:RetryMaxDelay"] = "00:00:01";
        builder.Configuration["Tasks:Worker:WorkerId"] = "file-drop-worker-test";
        builder.Configuration["Tasks:Worker:NodeId"] = "file-drop-worker-node";
        builder.Configuration["Tasks:Worker:TimeoutScannerEnabled"] = "false";
        builder.Configuration["Tasks:Worker:MetricsSamplerEnabled"] = "false";
        builder.Configuration["Worker:Modules:Ingestion"] = "true";
        builder.Configuration["Worker:Modules:TaskRuntime"] = "true";
        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "Minio";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] = "application/json";
        builder.Configuration["FileManagement:AllowedContentTypes:1"] = "message/rfc822";
        builder.Configuration["FileManagement:Minio:Endpoint"] = minioEndpoint;
        builder.Configuration["FileManagement:Minio:AccessKey"] = "minioadmin";
        builder.Configuration["FileManagement:Minio:SecretKey"] = "minioadmin";
        builder.Configuration["FileManagement:Minio:BucketName"] = minioBucket;
        builder.Configuration["FileManagement:Minio:UseSsl"] = "false";
        builder.Configuration["FileManagement:Minio:CreateBucketIfMissing"] = "true";
        builder.Configuration["Adapters:JsonFileDrop:RootPath"] = fileDropRoot;
        builder.Configuration["Adapters:JsonFileDrop:ProcessedArchiveRetention"] = "01:00:00";
        builder.Configuration["Adapters:JsonFileDrop:FailedQuarantineRetention"] = "01:00:00";
        builder.Configuration["Adapters:JsonFileDrop:MaximumDeletesPerRun"] = "10";
        builder.Configuration["Adapters:Materials:Configurations:file-drop:SchemaVersion"] = "1";
        builder.Configuration["Adapters:Materials:Configurations:file-drop:ContentType"] = "application/json";
        builder.Configuration["Adapters:Materials:Configurations:file-drop:Value"] = "{}";
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        Assert.True(result.IsValid, result.Report);
        return builder.Build();
    }

    private static async Task MigrateFileDropWorkerStoresAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task SeedFileDropConnectionAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        Guid connectionId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        IntegrationEventSubscription subscription = scope.ServiceProvider
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions.Single(item => item.ConsumerModule == IngestionModuleMetadata.Name &&
                                          item.EventType == typeof(PropertyCreatedIntegrationEvent));
        var handler = (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)scope.ServiceProvider
            .GetRequiredService(subscription.HandlerType);
        IngestionDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<IngestionDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dbContext,
            token => handler.HandleAsync(
                new PropertyCreatedIntegrationEvent(
                    Guid.NewGuid(),
                    tenantId,
                    DateTimeOffset.UtcNow,
                    propertyId,
                    "File Drop House",
                    "file-drop-house",
                    "UTC",
                    PropertyStatus.Active,
                    1),
                token)).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            IngestionModuleMetadata.Name,
            tenantId,
            propertyId,
            2).ConfigureAwait(false);

        dbContext.AdapterConnections.Add(AdapterConnection.Create(
            connectionId,
            tenantId,
            propertyId,
            JsonFileDropAdapterDescriptor.AdapterType,
            AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://file-drop",
            secretReference: null,
            DateTimeOffset.UtcNow).Value);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task EnqueueFileDropTaskAsync(
        IHost worker,
        string tenantId,
        Guid connectionId,
        Guid runId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        ITaskRunStore store = scope.ServiceProvider.GetRequiredService<ITaskRunStore>();
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(
            new TaskRunRequest(
                runId,
                IngestionModuleMetadata.Name,
                RunAdapterTaskPayload.TaskName,
                JsonSerializer.Serialize(new RunAdapterTaskPayload(connectionId)),
                createdAtUtc,
                createdAtUtc,
                IngestionModuleMetadata.AdapterWorkerGroup,
                tenantId,
                requestedBy: "integration-test",
                maxAttempts: 1,
                payloadVersion: RunAdapterTaskPayload.PayloadVersion,
                deduplicationKey: $"file-drop:{connectionId:N}"),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<ObservationReprocessingPreparation> PrepareMailReprocessingAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        Guid connectionId)
    {
        using (IServiceScope scope = worker.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
            IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            dbContext.AdapterConnections.Add(AdapterConnection.Create(
                connectionId,
                tenantId,
                propertyId,
                ImapReservationMailAdapterDescriptor.AdapterType,
                AdapterExecutionMode.Push,
                IngestionConflictPolicy.SuggestionsOnly,
                "configuration://mail-reprocessing",
                secretReference: null,
                DateTimeOffset.UtcNow).Value);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        AdapterObservationResult accepted;
        using (IServiceScope scope = worker.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
            byte[] message = CreateReservationMail();
            Result<AdapterObservationResult> result = await scope.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .SendAsync(new ReceiveObservationCommand(
                    connectionId,
                    RunId: null,
                    Guid.NewGuid(),
                    "mail.unparsed.v1",
                    "mailbox:9:42",
                    "42",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow,
                    "message/rfc822",
                    message,
                    AdapterPayloadHash.ComputeSha256(message)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(result.IsSuccess, result.Error.Code);
            accepted = result.Value;
        }

        using (IServiceScope scope = worker.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
            IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            ObservationReceipt source = await dbContext.ObservationReceipts
                .SingleAsync(receipt => receipt.Id == accepted.ReceiptId).ConfigureAwait(false);
            AdapterConnection connection = await dbContext.AdapterConnections
                .SingleAsync(item => item.Id == connectionId).ConfigureAwait(false);
            Assert.True(source.Reject("unsupported source evidence", DateTimeOffset.UtcNow).IsSuccess);
            Assert.True(connection.Disable(connection.Version, DateTimeOffset.UtcNow).IsSuccess);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        using (IServiceScope scope = worker.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
            Result<ObservationReprocessingPreparation> prepared = await scope.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .SendAsync(new PrepareObservationReprocessingCommand(
                    propertyId,
                    accepted.ReceiptId!.Value,
                    ReservationMailParserDescriptor.ParserType,
                    ParserVersion: null,
                    "integration-test",
                    ScheduledAtUtc: null), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(prepared.IsSuccess, prepared.Error.Code);
            return prepared.Value;
        }
    }

    private static async Task EnqueueReprocessingTaskAsync(
        IHost worker,
        string tenantId,
        ObservationReprocessingPreparation prepared)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        ITaskRunStore store = scope.ServiceProvider.GetRequiredService<ITaskRunStore>();
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(new TaskRunRequest(
            prepared.TaskRunId,
            IngestionModuleMetadata.Name,
            ReprocessObservationPayload.TaskName,
            JsonSerializer.Serialize(new ReprocessObservationPayload(
                prepared.AttemptId,
                prepared.ParserType,
                prepared.ParserVersion,
                MaxAttempts: 3)),
            createdAtUtc,
            createdAtUtc,
            IngestionModuleMetadata.MaintenanceWorkerGroup,
            tenantId,
            correlationId: prepared.SourceReceiptId,
            requestedBy: "integration-test",
            maxAttempts: 3,
            payloadVersion: ReprocessObservationPayload.PayloadVersion,
            deduplicationKey: $"reprocess:{prepared.AttemptId:N}"), CancellationToken.None).ConfigureAwait(false);
    }

    private static byte[] CreateReservationMail()
    {
        MimeMessage message = new();
        message.From.Add(MailboxAddress.Parse("provider@example.test"));
        message.To.Add(MailboxAddress.Parse("reservations@example.test"));
        message.Subject = "Provider reservation";
        BodyBuilder body = new() { TextBody = "Reservation data attached." };
        body.Attachments.Add(
            "provider-specific-name.json",
            Encoding.UTF8.GetBytes(/*lang=json,strict*/ """
                {
                  "schemaVersion": 1,
                  "externalRecordId": "reprocessed-booking-42",
                  "sourceRevision": "1",
                  "sourceUpdatedAtUtc": "2026-07-12T13:00:00Z",
                  "payload": {
                    "operation": "upsert",
                    "sourceSequence": 1,
                    "arrival": "2026-08-01",
                    "departure": "2026-08-02",
                    "inventoryUnitIds": ["41000000-0000-0000-0000-000000000001"],
                    "primaryGuestName": "Reprocessed Guest",
                    "guestCount": 1
                  }
                }
                """),
            ContentType.Parse("application/json"));
        message.Body = body.ToMessageBody();
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private static async Task<TaskRunSnapshot> WaitForStatusAsync(
        IHost worker,
        Guid runId,
        TaskRunStatus expectedStatus,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TaskRunSnapshot snapshot = await GetSnapshotAsync(worker, runId).ConfigureAwait(false);
            if (snapshot.Status == expectedStatus)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return await GetSnapshotAsync(worker, runId).ConfigureAwait(false);
    }

    private static async Task<TaskRunSnapshot> GetSnapshotAsync(IHost worker, Guid runId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        TaskRuntimeDbContext dbContext = scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>();
        TaskRunSnapshot? snapshot = await dbContext.TaskRuns
            .Where(taskRun => taskRun.Id == runId)
            .Select(taskRun => new TaskRunSnapshot(
                taskRun.Id,
                taskRun.Status,
                taskRun.LockedBy,
                taskRun.NodeId,
                taskRun.Attempts,
                taskRun.NextAttemptAtUtc,
                taskRun.CompletedAtUtc,
                taskRun.LastError,
                taskRun.ProgressPercent,
                taskRun.ProgressMessage,
                taskRun.RequestedBy,
                taskRun.CancellationRequestedBy,
                taskRun.CancellationRequestedAtUtc,
                taskRun.PayloadVersion,
                taskRun.DeduplicationKey))
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);

        Assert.NotNull(snapshot);
        return snapshot;
    }
}
