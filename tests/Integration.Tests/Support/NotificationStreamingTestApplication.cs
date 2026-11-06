namespace Integration.Tests.Support;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Host.Api;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Gma.Framework.Notifications;
using Gma.Framework.Security;
using Gma.Framework.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

internal sealed class NotificationStreamingTestApplication(
    bool tenancyEnabled = true,
    string environmentName = "Integration",
    bool prometheusEnabled = false,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<ApiAssemblyReference>
{
    private const string JwtIssuer = "BunkFy";
    private const string JwtAudience = "BunkFy";
    private const string JwtSigningKey = "notification-streaming-test-signing-key-change-me-0000000000";
    private const string RefreshTokenPepper = "notification-streaming-test-refresh-token-pepper-change-me-000000";
    private readonly SignalRConnectionReadiness signalRConnectionReadiness = new();
    private readonly StationK0AuthenticationObserver stationAuthenticationObserver = new();
    private readonly StationK0EvidenceProbe stationEvidenceProbe = new();
    private readonly StationK0LogCollector stationEvidenceLogs = new();

    public IReadOnlyList<StationAuthenticationObservation> StationAuthenticationObservations =>
        this.stationAuthenticationObserver.Observations;

    public StationK0EvidenceProbe StationEvidenceProbe => this.stationEvidenceProbe;

    public StationK0LogCollector StationEvidenceLogs => this.stationEvidenceLogs;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        string tenancyEnabledValue = tenancyEnabled.ToString();

        builder.UseEnvironment(environmentName);
        builder.ConfigureLogging(logging => logging.AddProvider(this.stationEvidenceLogs));
        builder.UseSetting("ApplicationIdentity:Namespace", "test-app");
        builder.UseSetting("Persistence:Provider", "SqlServer");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=127.0.0.1,1433;Database=unused;User Id=sa;Password=Pass@word1;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0");
        builder.UseSetting("ConnectionStrings:PostgreSql", string.Empty);
        builder.UseSetting("ConnectionStrings:nats", string.Empty);
        builder.UseSetting("NatsJetStream:Enabled", "false");
        builder.UseSetting("Tenancy:Enabled", tenancyEnabledValue);
        builder.UseSetting("Notifications:Enabled", "true");
        builder.UseSetting("Notifications:Sse:Enabled", "true");
        builder.UseSetting("Notifications:Sse:HeartbeatInterval", "00:00:01");
        builder.UseSetting("Notifications:SignalR:Enabled", "true");
        builder.UseSetting("Caching:Enabled", "false");
        builder.UseSetting("Observability:Prometheus:Enabled", prometheusEnabled.ToString());
        builder.UseSetting("Http:HttpsRedirectionEnabled", "false");
        builder.UseSetting("FileManagement:Minio:Endpoint", "localhost:9000");
        builder.UseSetting("FileManagement:Minio:AccessKey", "minioadmin");
        builder.UseSetting("FileManagement:Minio:SecretKey", "minioadmin");
        builder.UseSetting("FileManagement:Minio:BucketName", "integration-test-files");
        builder.UseSetting("FileManagement:Minio:UseSsl", "false");
        builder.UseSetting("FileManagement:Minio:CreateBucketIfMissing", "false");
        builder.UseSetting("Auth:RefreshTokens:Pepper", RefreshTokenPepper);
        builder.UseSetting("Auth:Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Auth:Jwt:Audience", JwtAudience);
        builder.UseSetting("Auth:Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("Auth:Jwt:AccessTokenLifetimeMinutes", "15");
        builder.UseSetting("Auth:BearerAdmission:Mode", "TokenLifetime");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationIdentity:Namespace"] = "test-app",
                ["Persistence:Provider"] = "SqlServer",
                ["ConnectionStrings:SqlServer"] = "Server=127.0.0.1,1433;Database=unused;User Id=sa;Password=Pass@word1;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0",
                ["ConnectionStrings:PostgreSql"] = string.Empty,
                ["ConnectionStrings:nats"] = string.Empty,
                ["NatsJetStream:Enabled"] = "false",
                ["Tenancy:Enabled"] = tenancyEnabledValue,
                ["Notifications:Enabled"] = "true",
                ["Notifications:Sse:Enabled"] = "true",
                ["Notifications:Sse:HeartbeatInterval"] = "00:00:01",
                ["Notifications:SignalR:Enabled"] = "true",
                ["Caching:Enabled"] = "false",
                ["Observability:Prometheus:Enabled"] = prometheusEnabled.ToString(),
                ["Http:HttpsRedirectionEnabled"] = "false",
                ["FileManagement:Minio:Endpoint"] = "localhost:9000",
                ["FileManagement:Minio:AccessKey"] = "minioadmin",
                ["FileManagement:Minio:SecretKey"] = "minioadmin",
                ["FileManagement:Minio:BucketName"] = "integration-test-files",
                ["FileManagement:Minio:UseSsl"] = "false",
                ["FileManagement:Minio:CreateBucketIfMissing"] = "false",
                ["Auth:RefreshTokens:Pepper"] = RefreshTokenPepper,
                ["Auth:Jwt:Issuer"] = JwtIssuer,
                ["Auth:Jwt:Audience"] = JwtAudience,
                ["Auth:Jwt:SigningKey"] = JwtSigningKey,
                ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["Auth:BearerAdmission:Mode"] = "TokenLifetime",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserNotificationHistoryWriter>();
            services.RemoveAll<IUserNotificationDeliveryPolicyEvaluator>();
            services.RemoveAll<IStationAuthenticationObserver>();
            services.AddSingleton<IUserNotificationDeliveryPolicyEvaluator, AllowAllDeliveryPolicyEvaluator>();
            services.AddSingleton<IStationAuthenticationObserver>(this.stationAuthenticationObserver);
            services.AddSingleton(this.stationEvidenceProbe);
            services.AddSingleton<IStartupFilter, StationK0EvidenceStartupFilter>();
            services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, StationK0EvidenceJwtBearerPostConfigure>();
            services.AddSingleton(this.signalRConnectionReadiness);
            services.AddSingleton<SignalRConnectionReadyFilter>();
            services.Configure<HubOptions>(options => options.AddFilter<SignalRConnectionReadyFilter>());

            if (!tenancyEnabled)
            {
                services.PostConfigure<TenantOptions>(options => options.Enabled = false);
            }

            configureServices?.Invoke(services);
        });
    }

    private sealed class AllowAllDeliveryPolicyEvaluator : IUserNotificationDeliveryPolicyEvaluator
    {
        public ValueTask<bool> ShouldDeliverAsync(
            UserNotificationMessage message,
            string deliveryTag,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class SignalRConnectionReadyFilter(SignalRConnectionReadiness readiness) : IHubFilter
    {
        public async Task OnConnectedAsync(
            HubLifetimeContext context,
            Func<HubLifetimeContext, Task> next)
        {
            await next(context).ConfigureAwait(false);
            readiness.MarkReady();
        }
    }

    private sealed class SignalRConnectionReadiness
    {
        private readonly TaskCompletionSource ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => this.ready.Task;

        public void MarkReady() => this.ready.TrySetResult();
    }

    public static string CreateAccessToken(string? scopeId, string userId)
    {
        SymmetricSecurityKey securityKey = new(Encoding.UTF8.GetBytes(JwtSigningKey));
        SigningCredentials credentials = new(securityKey, SecurityAlgorithms.HmacSha256);
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId),
            new(ApplicationClaimNames.SessionId, Guid.NewGuid().ToString())
        ];
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            claims.Add(new Claim(ApplicationClaimNames.ScopeId, scopeId));
        }

        JwtSecurityToken token = new(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateStationShapedToken()
    {
        SymmetricSecurityKey securityKey = new(RandomNumberGenerator.GetBytes(32));
        SigningCredentials credentials = new(securityKey, SecurityAlgorithms.HmacSha256);
        JwtSecurityToken token = new(
            issuer: "urn:bunkfy:test:station",
            audience: "urn:bunkfy:test:public-api",
            claims:
            [
                new(JwtRegisteredClaimNames.Sub, "station-actor"),
                new(StationAuthenticationDefaults.EpochClaim, "1"),
                new("bfx_station_id", "station-1"),
                new("bfx_actor_id", "actor-1"),
                new("bfx_property_id", "property-1"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        token.Header[JwtHeaderParameterNames.Typ] = "bunkfy-station+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void ResetStationAuthenticationObservations()
    {
        this.stationAuthenticationObserver.Reset();
        this.stationEvidenceProbe.Reset();
        this.stationEvidenceLogs.Reset();
    }

    public async Task PublishAsync(
        string scopeId,
        string userId,
        string title,
        NotificationSeverity severity = NotificationSeverity.Info,
        CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = this.Services.CreateScope();
        IUserNotificationPublisher publisher = scope.ServiceProvider.GetRequiredService<IUserNotificationPublisher>();
        await publisher.PublishAsync(
                "test-module",
                UserNotificationTarget.User(scopeId, userId),
                new StreamingTestNotificationPayload(title),
                new NotificationPublishOptions(title, severity: severity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task WaitForSignalRConnectionAsync(CancellationToken cancellationToken = default) =>
        this.signalRConnectionReadiness.Ready.WaitAsync(cancellationToken);

    [NotificationName(Name)]
    [NotificationVersion(Version)]
    [NotificationDescription("Notification streaming integration-test payload.")]
    private sealed record StreamingTestNotificationPayload(string Title) : IUserNotificationPayload
    {
        public const string Name = "streaming.test";
        public const int Version = 1;
    }
}
