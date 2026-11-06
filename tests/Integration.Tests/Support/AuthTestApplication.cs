namespace Integration.Tests.Support;

using System.Text.Json;
using BunkFy.Host.Api;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.RateLimiting;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Application.Ports;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Notifications.Persistence;
using Gma.Modules.Organizations.Domain.Aggregates;
using Gma.Modules.Organizations.Domain.Enums;
using Gma.Modules.Organizations.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;

internal sealed class AuthTestApplication(
    string provider,
    string providerConnectionString,
    string natsConnectionString,
    bool disableOutboxPublisher = true,
    bool enablePrometheus = false,
    string minioEndpoint = "localhost:9000",
    string minioAccessKey = "minioadmin",
    string minioSecretKey = "minioadmin",
    string minioBucketName = "integration-test-files",
    bool minioCreateBucketIfMissing = false,
    DbCommandInterceptor? inventoryCommandInterceptor = null,
    bool enableWorkspaceSelfService = false,
    string? adapterIngressRedisConnectionString = null,
    ISystemClock? systemClock = null,
    DbCommandInterceptor? staffCommandInterceptor = null,
    bool enableWorkspaceStaffOnboardingRecovery = false,
    IReadOnlyDictionary<string, string?>? configurationOverrides = null,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<ApiAssemblyReference>
{
    private const string JwtIssuer = "BunkFy";
    private const string JwtAudience = "BunkFy";
    internal const string JwtSigningKey = "integration-test-signing-key-change-me-000000000000000000";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Integration");
        builder.UseSetting("Persistence:Provider", provider);
        builder.UseSetting("ConnectionStrings:SqlServer", provider == "SqlServer" ? providerConnectionString : string.Empty);
        builder.UseSetting("ConnectionStrings:PostgreSql", provider == "PostgreSql" ? providerConnectionString : string.Empty);
        builder.UseSetting("ConnectionStrings:nats", natsConnectionString);
        builder.UseSetting("NatsJetStream:Enabled", disableOutboxPublisher ? "false" : "true");
        builder.UseSetting("Tenancy:Enabled", "true");
        builder.UseSetting("Outbox:PollIntervalMilliseconds", "100");
        builder.UseSetting("Outbox:LockDurationMilliseconds", "1000");
        builder.UseSetting("Observability:Prometheus:Enabled", enablePrometheus.ToString());
        builder.UseSetting("Caching:Enabled", "false");
        builder.UseSetting("Notifications:Delivery:Enabled", "false");
        builder.UseSetting("Notifications:DurableStreams:MonitorEnabled", "false");
        builder.UseSetting("FileManagement:Minio:Endpoint", minioEndpoint);
        builder.UseSetting("FileManagement:Minio:AccessKey", minioAccessKey);
        builder.UseSetting("FileManagement:Minio:SecretKey", minioSecretKey);
        builder.UseSetting("FileManagement:Minio:BucketName", minioBucketName);
        builder.UseSetting("FileManagement:Minio:UseSsl", "false");
        builder.UseSetting("FileManagement:Minio:CreateBucketIfMissing", minioCreateBucketIfMissing.ToString());
        builder.UseSetting("FileManagement:AllowedContentTypes:0", "application/json");
        builder.UseSetting("FileManagement:AllowedContentTypes:1", "message/rfc822");
        builder.UseSetting("FileManagement:AllowedContentTypes:2", "application/octet-stream");
        builder.UseSetting("Auth:Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Auth:Jwt:Audience", JwtAudience);
        builder.UseSetting("Auth:Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("Auth:Jwt:AccessTokenLifetimeMinutes", "15");
        builder.UseSetting("Auth:BearerAdmission:Mode", "ActiveSession");
        builder.UseSetting("Auth:RefreshTokens:Pepper", AuthTestConfiguration.RefreshTokenPepper);
        builder.UseSetting("Auth:SelfRegistration:PasswordEnabled", "true");
        builder.UseSetting("Auth:SelfRegistration:ExternalEnabled", "true");
        builder.UseSetting(
            "BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled",
            enableWorkspaceStaffOnboardingRecovery.ToString());
        if (configurationOverrides is not null)
        {
            foreach ((string key, string? value) in configurationOverrides)
            {
                if (value is not null)
                {
                    builder.UseSetting(key, value);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(adapterIngressRedisConnectionString))
        {
            builder.UseSetting("Ingestion:AdapterIngress:Enabled", "true");
            builder.UseSetting("ConnectionStrings:redis", adapterIngressRedisConnectionString);
        }

        if (enableWorkspaceSelfService)
        {
            builder.UseSetting("Organizations:SelfServiceCreationEnabled", "true");
            builder.UseSetting("BunkFy:WorkspaceAdmission:AccountRegistration", "Public");
            builder.UseSetting("BunkFy:WorkspaceAdmission:WorkspaceCreation", "SelfService");
            builder.UseSetting("BunkFy:WorkspaceAdmission:RequireVerifiedEmailForWorkspaceCreation", "true");
            builder.UseSetting("Http:RateLimiting:GlobalPermitLimit", "1000");
            builder.UseSetting("Http:RateLimiting:SensitivePermitLimit", "1000");
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Persistence:Provider"] = provider,
                ["ConnectionStrings:SqlServer"] = provider == "SqlServer" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:PostgreSql"] = provider == "PostgreSql" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:nats"] = natsConnectionString,
                ["Auth:RefreshTokens:Pepper"] = AuthTestConfiguration.RefreshTokenPepper,
                ["Auth:Jwt:Issuer"] = JwtIssuer,
                ["Auth:Jwt:Audience"] = JwtAudience,
                ["Auth:Jwt:SigningKey"] = JwtSigningKey,
                ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["Auth:BearerAdmission:Mode"] = "ActiveSession",
                ["Auth:SelfRegistration:PasswordEnabled"] = "true",
                ["Auth:SelfRegistration:ExternalEnabled"] = "true",
                ["BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled"] =
                    enableWorkspaceStaffOnboardingRecovery.ToString(),
                ["NatsJetStream:Enabled"] = disableOutboxPublisher ? "false" : "true",
                ["Tenancy:Enabled"] = "true",
                ["Outbox:PollIntervalMilliseconds"] = "100",
                ["Outbox:LockDurationMilliseconds"] = "1000",
                ["Observability:Prometheus:Enabled"] = enablePrometheus.ToString(),
                ["Notifications:Delivery:Enabled"] = "false",
                ["Notifications:DurableStreams:MonitorEnabled"] = "false",
                ["FileManagement:Minio:Endpoint"] = minioEndpoint,
                ["FileManagement:Minio:AccessKey"] = minioAccessKey,
                ["FileManagement:Minio:SecretKey"] = minioSecretKey,
                ["FileManagement:Minio:BucketName"] = minioBucketName,
                ["FileManagement:Minio:UseSsl"] = "false",
                ["FileManagement:Minio:CreateBucketIfMissing"] = minioCreateBucketIfMissing.ToString(),
                ["FileManagement:AllowedContentTypes:0"] = "application/json",
                ["FileManagement:AllowedContentTypes:1"] = "message/rfc822",
                ["FileManagement:AllowedContentTypes:2"] = "application/octet-stream",
            };

            if (!string.IsNullOrWhiteSpace(adapterIngressRedisConnectionString))
            {
                values["Ingestion:AdapterIngress:Enabled"] = "true";
                values["ConnectionStrings:redis"] = adapterIngressRedisConnectionString;
            }

            if (enableWorkspaceSelfService)
            {
                values["Organizations:SelfServiceCreationEnabled"] = "true";
                values["BunkFy:WorkspaceAdmission:AccountRegistration"] = "Public";
                values["BunkFy:WorkspaceAdmission:WorkspaceCreation"] = "SelfService";
                values["BunkFy:WorkspaceAdmission:RequireVerifiedEmailForWorkspaceCreation"] = "true";
                values["Http:RateLimiting:GlobalPermitLimit"] = "1000";
                values["Http:RateLimiting:SensitivePermitLimit"] = "1000";
            }

            if (configurationOverrides is not null)
            {
                foreach ((string key, string? value) in configurationOverrides)
                {
                    values[key] = value;
                }
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            CountryPolicyIntegrationTestData.InstallRegistry(services);

            if (systemClock is not null)
            {
                services.RemoveAll<ISystemClock>();
                services.AddSingleton(systemClock);
            }

            if (disableOutboxPublisher)
            {
                ServiceDescriptor[] hostedServicesToRemove = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType?.Name == "OutboxPublisherService")
                    .ToArray();

                foreach (ServiceDescriptor hostedService in hostedServicesToRemove)
                {
                    services.Remove(hostedService);
                }
            }

            services.RemoveAll<INatsConnection>();
            services.AddSingleton<INatsConnection>(_ => new NatsConnection(new NatsOpts
            {
                Url = natsConnectionString,
            }));

            if (inventoryCommandInterceptor is not null)
            {
                services.AddDbContext<InventoryDbContext>(options =>
                    options.AddInterceptors(inventoryCommandInterceptor));
            }

            if (staffCommandInterceptor is not null)
            {
                services.AddDbContext<StaffDbContext>(options =>
                    options.AddInterceptors(staffCommandInterceptor));
            }

            configureServices?.Invoke(services);
        });
    }

    public async Task MigrateDatabaseAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = provider,
                ["ConnectionStrings:SqlServer"] = provider == "SqlServer" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:PostgreSql"] = provider == "PostgreSql" ? providerConnectionString : string.Empty,
            })
            .Build();
        DbContextOptionsBuilder<AuthDbContext> options = new();
        options.UseConfiguredProvider(
            configuration,
            AuthMigrations.SqlServerAssembly,
            AuthMigrations.PostgreSqlAssembly,
            AuthMigrations.Schema,
            AuthMigrations.HistoryTable);

        await using AuthDbContext dbContext = new(options.Options, DisabledTenantContext.Instance);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigratePropertiesAuthorizationDatabaseAsync(
        string? propertiesTargetMigration = null)
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigratePropertiesAuthorizationDatabaseAsync(
                    propertiesTargetMigration)).ConfigureAwait(false);
            return;
        }

        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AccessControlDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        PropertiesDbContext properties =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        if (propertiesTargetMigration is null)
        {
            await properties.Database.MigrateAsync().ConfigureAwait(false);
        }
        else
        {
            await properties.Database.GetService<IMigrator>()
                .MigrateAsync(propertiesTargetMigration)
                .ConfigureAwait(false);
        }
        await this.MigrateWorkspaceAdmissionDatabaseAsync().ConfigureAwait(false);
    }

    public async Task MigrateInventoryAuthorizationDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateInventoryAuthorizationDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigratePropertiesAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateReservationsAuthorizationDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateReservationsAuthorizationDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigrateInventoryAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateGuestRecordsAuthorizationDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateGuestRecordsAuthorizationDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigrateReservationsAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GuestsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateGuestDataRightsAuthorizationDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateGuestDataRightsAuthorizationDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        using (IServiceScope notificationScope = this.Services.CreateScope())
        {
            await notificationScope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
                .Database.MigrateAsync().ConfigureAwait(false);
        }

        await this.MigrateGuestRecordsAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DataRightsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateStaffAuthorizationDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateStaffAuthorizationDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigratePropertiesAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StaffDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateWorkspaceOnboardingDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateWorkspaceOnboardingDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigrateStaffAuthorizationDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateIngestionDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateIngestionDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigrateWorkspaceAdmissionDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigrateRetentionDatabaseAsync()
    {
        if (!disableOutboxPublisher)
        {
            await this.RunMigrationWithOutboxPublisherDisabledAsync(
                application => application.MigrateRetentionDatabaseAsync())
                .ConfigureAwait(false);
            return;
        }

        await this.MigrateWorkspaceAdmissionDatabaseAsync().ConfigureAwait(false);
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RetentionDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task RunMigrationWithOutboxPublisherDisabledAsync(
        Func<AuthTestApplication, Task> migration)
    {
        ArgumentNullException.ThrowIfNull(migration);

        await using AuthTestApplication migrationApplication = new(
            provider,
            providerConnectionString,
            natsConnectionString,
            disableOutboxPublisher: true,
            enablePrometheus: enablePrometheus,
            minioEndpoint: minioEndpoint,
            minioAccessKey: minioAccessKey,
            minioSecretKey: minioSecretKey,
            minioBucketName: minioBucketName,
            minioCreateBucketIfMissing: minioCreateBucketIfMissing,
            inventoryCommandInterceptor: inventoryCommandInterceptor,
            enableWorkspaceSelfService: enableWorkspaceSelfService,
            adapterIngressRedisConnectionString: adapterIngressRedisConnectionString,
            systemClock: systemClock,
            staffCommandInterceptor: staffCommandInterceptor,
            enableWorkspaceStaffOnboardingRecovery: enableWorkspaceStaffOnboardingRecovery,
            configurationOverrides: configurationOverrides,
            configureServices: configureServices);
        await migration(migrationApplication).ConfigureAwait(false);
    }

    private async Task MigrateWorkspaceAdmissionDatabaseAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public WebApplicationFactory<ApiAssemblyReference> WithUnavailableAdapterIngressProvider() =>
        this.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMultiPartitionRateLimiter>();
            services.AddSingleton<IMultiPartitionRateLimiter, UnavailableRateLimiter>();
        }));

    public async Task SeedOrganizationMembershipAsync(string tenantId, Guid subjectId)
    {
        if (!Guid.TryParse(tenantId, out Guid organizationId) || organizationId == Guid.Empty)
        {
            throw new ArgumentException("The product tenant id must be a non-empty organization id.", nameof(tenantId));
        }

        using IServiceScope scope = this.Services.CreateScope();
        OrganizationsDbContext dbContext = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        bool organizationExists = await dbContext.Organizations
            .AnyAsync(item => item.Id == organizationId)
            .ConfigureAwait(false);
        string subject = subjectId.ToString("D");
        string actor = $"user:{subject}";
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        if (!organizationExists)
        {
            Organization organization = Organization.Create(
                organizationId,
                $"Integration {organizationId:N}",
                $"integration-{organizationId:N}",
                actor,
                Guid.NewGuid(),
                nowUtc).Value;
            dbContext.Organizations.Add(organization);
        }

        bool membershipExists = await dbContext.Memberships
            .AnyAsync(item => item.OrganizationId == organizationId && item.SubjectId == subject)
            .ConfigureAwait(false);
        if (!membershipExists)
        {
            OrganizationMembership membership = OrganizationMembership.Create(
                Guid.NewGuid(),
                organizationId,
                subject,
                organizationExists ? OrganizationMembershipRole.Member : OrganizationMembershipRole.Owner,
                actor,
                Guid.NewGuid(),
                nowUtc).Value;
            dbContext.Memberships.Add(membership);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private sealed class UnavailableRateLimiter : IMultiPartitionRateLimiter
    {
        public ValueTask<MultiPartitionRateLimitDecision> AcquireAsync(
            MultiPartitionRateLimitRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MultiPartitionRateLimitDecision.ProviderUnavailable());
    }

    public async Task SetOrganizationMembershipSuspendedAsync(
        string tenantId,
        Guid subjectId,
        bool suspended)
    {
        if (!Guid.TryParse(tenantId, out Guid organizationId) || organizationId == Guid.Empty)
        {
            throw new ArgumentException("The product tenant id must be a non-empty organization id.", nameof(tenantId));
        }

        using IServiceScope scope = this.Services.CreateScope();
        OrganizationsDbContext dbContext = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        string subject = subjectId.ToString("D");
        OrganizationMembership membership = await dbContext.Memberships
            .SingleAsync(item => item.OrganizationId == organizationId && item.SubjectId == subject)
            .ConfigureAwait(false);
        string actor = $"user:{subject}";
        Result result = suspended
            ? membership.Suspend(membership.Version, actor, Guid.NewGuid(), DateTimeOffset.UtcNow)
            : membership.Resume(membership.Version, actor, Guid.NewGuid(), DateTimeOffset.UtcNow);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<Guid> AddOutboxMessageAsync(DateTimeOffset nowUtc)
    {
        using IServiceScope scope = this.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Guid id = Guid.NewGuid();

        dbContext.OutboxMessages.Add(new Gma.Framework.Messaging.Infrastructure.OutboxMessage(
            id,
            "gma.auth.test.v1",
            "Integration.Tests.TestEvent",
            1,
            "tenant-outbox",
            nowUtc,
            "{}",
            nowUtc));

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return id;
    }

    public async Task<IReadOnlyList<OutboxMessageRecord>> ClaimOutboxAsync(
        string workerId,
        DateTimeOffset nowUtc,
        TimeSpan lockDuration)
    {
        using IServiceScope scope = this.Services.CreateScope();
        IOutboxStore store = scope.ServiceProvider.GetServices<IOutboxStore>().Single(item => item.ModuleName == "auth");

        return await store.ClaimPendingAsync(25, workerId, nowUtc, lockDuration, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async Task MarkOutboxProcessedAsync(Guid id, string workerId, DateTimeOffset nowUtc)
    {
        using IServiceScope scope = this.Services.CreateScope();
        IOutboxStore store = scope.ServiceProvider.GetServices<IOutboxStore>().Single(item => item.ModuleName == "auth");

        await store.MarkProcessedAsync(id, workerId, nowUtc, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task MarkOutboxFailedAsync(Guid id, string workerId, string error, DateTimeOffset nowUtc)
    {
        using IServiceScope scope = this.Services.CreateScope();
        IOutboxStore store = scope.ServiceProvider.GetServices<IOutboxStore>().Single(item => item.ModuleName == "auth");

        await store.MarkFailedAsync(id, workerId, error, nowUtc, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<int> CountPendingOutboxMessagesAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await dbContext.OutboxMessages
            .CountAsync(message => message.ProcessedAtUtc == null)
            .ConfigureAwait(false);
    }

    public async Task<int> CountProcessedOutboxMessagesAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await dbContext.OutboxMessages
            .CountAsync(message => message.ProcessedAtUtc != null)
            .ConfigureAwait(false);
    }

    public async Task<int> WaitForProcessedOutboxMessagesAsync(int expectedCount, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            int processed = await this.CountProcessedOutboxMessagesAsync().ConfigureAwait(false);

            if (processed >= expectedCount)
            {
                return processed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return await this.CountProcessedOutboxMessagesAsync().ConfigureAwait(false);
    }

    public async Task<string> WaitForEmailVerificationCodeAsync(Guid memberId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        string memberIdText = memberId.ToString("D");

        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = this.Services.CreateScope();
            AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            string? payload = await dbContext.OutboxMessages
                .AsNoTracking()
                .Where(message =>
                    message.EventType == typeof(MemberEmailVerificationRequestedIntegrationEvent).FullName &&
                    message.Payload.Contains(memberIdText))
                .OrderByDescending(message => message.CreatedAtUtc)
                .Select(message => message.Payload)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            if (payload is not null)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (TryGetString(document.RootElement, "verificationCode", out string? code))
                {
                    return code!;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new TimeoutException($"Auth did not publish an email verification code for member {memberId:D}.");
    }

    public async Task<OutboxSnapshot> GetOutboxSnapshotAsync(Guid id)
    {
        using IServiceScope scope = this.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        OutboxSnapshot? snapshot = await dbContext.OutboxMessages
            .Where(message => message.Id == id)
            .Select(message => new OutboxSnapshot(
                message.Id,
                message.ProcessedAtUtc,
                message.LockedBy,
                message.LockedUntilUtc,
                message.NextAttemptAtUtc,
                message.Attempts,
                message.Error))
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);

        Xunit.Assert.NotNull(snapshot);
        return snapshot;
    }

    public async Task<OutboxSnapshot> GetOnlyOutboxSnapshotAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await dbContext.OutboxMessages
            .AsNoTracking()
            .Select(message => new OutboxSnapshot(
                message.Id,
                message.ProcessedAtUtc,
                message.LockedBy,
                message.LockedUntilUtc,
                message.NextAttemptAtUtc,
                message.Attempts,
                message.Error))
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private sealed class DisabledTenantContext : IAuthScopeContext
    {
        public static readonly DisabledTenantContext Instance = new();

        public bool IsEnabled => false;
        public string? ScopeId => null;
        public bool TryRestoreScope(string? scopeId) => true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }
}
