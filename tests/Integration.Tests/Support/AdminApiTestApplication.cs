namespace Integration.Tests.Support;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BunkFy.Host.AdminApi;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Security;
using Gma.Modules.AccessControl.Application.Commands;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Administration.Persistence;
using Gma.Modules.Administration.Persistence.Entities;
using Gma.Modules.Auth.Application.Ports;
using Gma.Modules.Auth.Domain.Aggregates;
using Gma.Modules.Auth.Domain.Enums;
using Gma.Modules.Auth.Domain.Repositories;
using Gma.Modules.Auth.Domain.Services;
using Gma.Modules.Auth.Domain.ValueObjects;
using Gma.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NATS.Client.Core;

internal sealed class AdminApiTestApplication(
    string provider,
    string providerConnectionString,
    string natsConnectionString,
    bool disableOutboxPublisher = true,
    bool allowGeneratedPasswordResponses = false,
    ISystemClock? systemClock = null,
    bool disableDatabaseBackgroundServices = false,
    Action<IServiceCollection>? configureServices = null,
    bool useActiveSessionAdmission = false)
    : WebApplicationFactory<AdminApiAssemblyReference>
{
    private const string JwtIssuer = "BunkFy";
    private const string JwtAudience = "BunkFy";
    private const string JwtSigningKey = "integration-test-signing-key-change-me-000000000000000000";
    private readonly StationK0AuthenticationObserver stationAuthenticationObserver = new();
    private readonly StationK0EvidenceProbe stationEvidenceProbe = new();
    private readonly StationK0LogCollector stationEvidenceLogs = new();

    public IReadOnlyList<StationAuthenticationObservation> StationAuthenticationObservations =>
        this.stationAuthenticationObserver.Observations;

    public StationK0EvidenceProbe StationEvidenceProbe => this.stationEvidenceProbe;

    public StationK0LogCollector StationEvidenceLogs => this.stationEvidenceLogs;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Integration");
        builder.ConfigureLogging(logging => logging.AddProvider(this.stationEvidenceLogs));
        builder.UseSetting("Persistence:Provider", provider);
        builder.UseSetting("ConnectionStrings:SqlServer", provider == "SqlServer" ? providerConnectionString : string.Empty);
        builder.UseSetting("ConnectionStrings:PostgreSql", provider == "PostgreSql" ? providerConnectionString : string.Empty);
        builder.UseSetting("ConnectionStrings:nats", natsConnectionString);
        builder.UseSetting("NatsJetStream:Enabled", disableOutboxPublisher ? "false" : "true");
        builder.UseSetting("Tenancy:Enabled", "true");
        builder.UseSetting("Outbox:PollIntervalMilliseconds", "100");
        builder.UseSetting("Outbox:LockDurationMilliseconds", "1000");
        builder.UseSetting("Auth:RefreshTokenLifetimeDays", "30");
        builder.UseSetting("Auth:RefreshTokens:Pepper", AuthTestConfiguration.RefreshTokenPepper);
        builder.UseSetting("Auth:Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Auth:Jwt:Audience", JwtAudience);
        builder.UseSetting("Auth:Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("Auth:Jwt:AccessTokenLifetimeMinutes", "15");
        builder.UseSetting(
            "Auth:BearerAdmission:Mode",
            useActiveSessionAdmission ? "ActiveSession" : "TokenLifetime");
        builder.UseSetting(
            "Administration:Api:AllowGeneratedPasswordResponses",
            allowGeneratedPasswordResponses.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Caching:Enabled", "false");
        builder.UseSetting("FileManagement:Enabled", "true");
        builder.UseSetting("FileManagement:Provider", "Minio");
        builder.UseSetting("FileManagement:MaximumObjectBytes", "67108864");
        builder.UseSetting("FileManagement:AllowedContentTypes:0", "application/json");
        builder.UseSetting("FileManagement:AllowedContentTypes:1", "application/octet-stream");
        builder.UseSetting("FileManagement:Minio:Endpoint", "localhost:9000");
        builder.UseSetting("FileManagement:Minio:AccessKey", "integration-test");
        builder.UseSetting("FileManagement:Minio:SecretKey", "integration-test-secret");
        builder.UseSetting("FileManagement:Minio:BucketName", "integration-test");
        builder.UseSetting("FileManagement:Minio:UseSsl", "false");
        builder.UseSetting("FileManagement:Minio:CreateBucketIfMissing", "false");
        builder.UseSetting("Http:PrivateNetwork:Enabled", "false");
        builder.UseSetting("Http:RateLimiting:Enabled", "false");
        builder.UseSetting("Http:HttpsRedirectionEnabled", "false");
        if (disableDatabaseBackgroundServices)
        {
            builder.UseSetting("Notifications:Delivery:Enabled", "false");
            builder.UseSetting("Notifications:DurableStreams:MonitorEnabled", "false");
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Persistence:Provider"] = provider,
                ["ConnectionStrings:SqlServer"] = provider == "SqlServer" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:PostgreSql"] = provider == "PostgreSql" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:nats"] = natsConnectionString,
                ["NatsJetStream:Enabled"] = disableOutboxPublisher ? "false" : "true",
                ["Tenancy:Enabled"] = "true",
                ["Outbox:PollIntervalMilliseconds"] = "100",
                ["Outbox:LockDurationMilliseconds"] = "1000",
                ["Auth:RefreshTokenLifetimeDays"] = "30",
                ["Auth:RefreshTokens:Pepper"] = AuthTestConfiguration.RefreshTokenPepper,
                ["Auth:Jwt:Issuer"] = JwtIssuer,
                ["Auth:Jwt:Audience"] = JwtAudience,
                ["Auth:Jwt:SigningKey"] = JwtSigningKey,
                ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["Auth:BearerAdmission:Mode"] = useActiveSessionAdmission
                    ? "ActiveSession"
                    : "TokenLifetime",
                ["Administration:Api:AllowGeneratedPasswordResponses"] = allowGeneratedPasswordResponses.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Caching:Enabled"] = "false",
                ["FileManagement:Enabled"] = "true",
                ["FileManagement:Provider"] = "Minio",
                ["FileManagement:MaximumObjectBytes"] = "67108864",
                ["FileManagement:AllowedContentTypes:0"] = "application/json",
                ["FileManagement:AllowedContentTypes:1"] = "application/octet-stream",
                ["FileManagement:Minio:Endpoint"] = "localhost:9000",
                ["FileManagement:Minio:AccessKey"] = "integration-test",
                ["FileManagement:Minio:SecretKey"] = "integration-test-secret",
                ["FileManagement:Minio:BucketName"] = "integration-test",
                ["FileManagement:Minio:UseSsl"] = "false",
                ["FileManagement:Minio:CreateBucketIfMissing"] = "false",
                ["Http:PrivateNetwork:Enabled"] = "false",
                ["Http:RateLimiting:Enabled"] = "false",
                ["Http:HttpsRedirectionEnabled"] = "false"
            };

            if (disableDatabaseBackgroundServices)
            {
                values["Notifications:Delivery:Enabled"] = "false";
                values["Notifications:DurableStreams:MonitorEnabled"] = "false";
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
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

            if (disableDatabaseBackgroundServices)
            {
                ServiceDescriptor[] databaseHostedServices = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType?.Name == "TaskRuntimeRetentionService")
                    .ToArray();

                foreach (ServiceDescriptor hostedService in databaseHostedServices)
                {
                    services.Remove(hostedService);
                }
            }

            services.RemoveAll<INatsConnection>();
            services.AddSingleton<INatsConnection>(_ => new NatsConnection(new NatsOpts
            {
                Url = natsConnectionString,
            }));
            services.RemoveAll<IStationAuthenticationObserver>();
            services.AddSingleton<IStationAuthenticationObserver>(this.stationAuthenticationObserver);
            services.AddSingleton(this.stationEvidenceProbe);
            services.AddSingleton<IStartupFilter, StationK0EvidenceStartupFilter>();
            services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, StationK0EvidenceJwtBearerPostConfigure>();
            configureServices?.Invoke(services);
        });
    }

    public void ResetStationAuthenticationObservations()
    {
        this.stationAuthenticationObserver.Reset();
        this.stationEvidenceProbe.Reset();
        this.stationEvidenceLogs.Reset();
    }

    public async Task MigrateAsync()
    {
        IConfiguration configuration = this.CreatePersistenceConfiguration();

        DbContextOptionsBuilder<AdminDbContext> adminOptions = new();
        adminOptions.UseConfiguredProvider(
            configuration,
            AdminMigrations.SqlServerAssembly,
            AdminMigrations.PostgreSqlAssembly,
            AdminMigrations.Schema,
            AdminMigrations.HistoryTable);
        await using AdminDbContext adminDbContext = new(adminOptions.Options);
        await adminDbContext.Database.MigrateAsync().ConfigureAwait(false);

        DbContextOptionsBuilder<AccessControlDbContext> accessControlOptions = new();
        accessControlOptions.UseConfiguredProvider(
            configuration,
            AccessControlMigrations.SqlServerAssembly,
            AccessControlMigrations.PostgreSqlAssembly,
            AccessControlMigrations.Schema,
            AccessControlMigrations.HistoryTable);
        await using AccessControlDbContext accessControlDbContext = new(accessControlOptions.Options);
        await accessControlDbContext.Database.MigrateAsync().ConfigureAwait(false);

        DbContextOptionsBuilder<AuthDbContext> authOptions = new();
        authOptions.UseConfiguredProvider(
            configuration,
            AuthMigrations.SqlServerAssembly,
            AuthMigrations.PostgreSqlAssembly,
            AuthMigrations.Schema,
            AuthMigrations.HistoryTable);
        await using AuthDbContext authDbContext = new(authOptions.Options, DisabledTenantContext.Instance);
        await authDbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task MigratePropertiesAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<PropertiesDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task SeedOwnerAsync(Guid actorId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();

        var result = await dispatcher
            .SendAsync(new BootstrapOwnerCommand(actorId.ToString(), Confirmed: true), CancellationToken.None)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    public async Task<string> CreatePersistedGlobalAccessTokenAsync(Guid actorId)
    {
        const string globalScopeId = "default";
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        SessionAuthenticationEvidence authenticationEvidence =
            SessionAuthenticationEvidence.CompleteWithTotp(
                SessionAuthenticationEvidence.Password(nowUtc),
                nowUtc);

        using IServiceScope scope = this.Services.CreateScope();
        IPasswordHashingService passwordHashingService =
            scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();
        IRefreshTokenHashingService refreshTokenHashingService =
            scope.ServiceProvider.GetRequiredService<IRefreshTokenHashingService>();
        IMemberRepository memberRepository =
            scope.ServiceProvider.GetRequiredService<IMemberRepository>();
        AuthDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ITokenService tokenService =
            scope.ServiceProvider.GetRequiredService<ITokenService>();

        var memberResult = Member.Create(
            new MemberId(actorId),
            globalScopeId,
            $"admin-{actorId:N}@example.com",
            MemberUsernameType.Email,
            passwordHashingService.HashPassword("Passw0rd!admin-session"),
            new MemberUsernameId(Guid.NewGuid()),
            Guid.NewGuid(),
            nowUtc);
        if (memberResult.IsFailure)
        {
            throw new InvalidOperationException(memberResult.Error.Message);
        }

        Member member = memberResult.Value;
        MemberSessionId sessionId = new(Guid.NewGuid());
        var sessionResult = member.StartSession(
            sessionId,
            refreshTokenHashingService.HashRefreshToken(Guid.NewGuid().ToString("N")),
            nowUtc.AddDays(30),
            nowUtc,
            authenticationEvidence: authenticationEvidence,
            maximumActiveSessions: 1,
            absoluteExpiresAtUtc: nowUtc.AddDays(90));
        if (sessionResult.IsFailure)
        {
            throw new InvalidOperationException(sessionResult.Error.Message);
        }

        await memberRepository.AddAsync(member, CancellationToken.None)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return tokenService.GenerateAccessToken(new AccessTokenClaims(
            member.Id,
            globalScopeId,
            sessionId,
            authenticationEvidence));
    }

    public async Task<int> CountAuditEntriesAsync(string operation, string? errorCode = null)
    {
        IConfiguration configuration = this.CreatePersistenceConfiguration();
        DbContextOptionsBuilder<AdminDbContext> options = new();
        options.UseConfiguredProvider(
            configuration,
            AdminMigrations.SqlServerAssembly,
            AdminMigrations.PostgreSqlAssembly,
            AdminMigrations.Schema,
            AdminMigrations.HistoryTable);
        await using AdminDbContext dbContext = new(options.Options);

        IQueryable<AdminAuditEntry> query = dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.Operation == operation);

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            query = query.Where(entry => entry.ErrorCode == errorCode);
        }

        return await query.CountAsync().ConfigureAwait(false);
    }

    public async Task<int> CountAuditEntriesContainingAsync(string value)
    {
        IConfiguration configuration = this.CreatePersistenceConfiguration();
        DbContextOptionsBuilder<AdminDbContext> options = new();
        options.UseConfiguredProvider(
            configuration,
            AdminMigrations.SqlServerAssembly,
            AdminMigrations.PostgreSqlAssembly,
            AdminMigrations.Schema,
            AdminMigrations.HistoryTable);
        await using AdminDbContext dbContext = new(options.Options);

        return await dbContext.AuditEntries
            .AsNoTracking()
            .CountAsync(entry =>
                entry.ActorId.Contains(value) ||
                (entry.TenantId != null && entry.TenantId.Contains(value)) ||
                entry.Operation.Contains(value) ||
                entry.Permission.Contains(value) ||
                entry.Result.Contains(value) ||
                (entry.ErrorCode != null && entry.ErrorCode.Contains(value)))
            .ConfigureAwait(false);
    }

    public string CreateAccessToken(Guid actorId, string scopeId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        ITokenService tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        return tokenService.GenerateAccessToken(new AccessTokenClaims(
            new MemberId(actorId),
            scopeId,
            new MemberSessionId(Guid.NewGuid()),
            SessionAuthenticationEvidence.CompleteWithTotp(
                SessionAuthenticationEvidence.Password(nowUtc),
                nowUtc)));
    }

    public static string CreateAccessTokenWithoutTenantClaim(Guid actorId)
    {
        return CreateJwt(actorId, scopeId: null);
    }

    public static string CreateAccessTokenWithTenantClaim(Guid actorId, string scopeId)
    {
        return CreateJwt(actorId, scopeId);
    }

    public static string CreateAccessTokenWithActorClaim(string actorId, string? scopeId)
    {
        return CreateJwt(actorId, scopeId);
    }

    private static string CreateJwt(Guid actorId, string? scopeId)
    {
        return CreateJwt(actorId.ToString(), scopeId);
    }

    private static string CreateJwt(string actorId, string? scopeId)
    {
        SymmetricSecurityKey securityKey = new(Encoding.UTF8.GetBytes(JwtSigningKey));
        SigningCredentials signingCredentials = new(securityKey, SecurityAlgorithms.HmacSha256);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, actorId),
            new(
                ApplicationClaimNames.AuthenticationContextReference,
                AuthenticationContextReferences.MultiFactor),
            new(
                ApplicationClaimNames.AuthenticationTime,
                nowUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new(
                ApplicationClaimNames.AuthenticationMethodReference,
                AuthenticationMethodReferences.Password),
            new(
                ApplicationClaimNames.AuthenticationMethodReference,
                AuthenticationMethodReferences.OneTimePassword),
            new(
                ApplicationClaimNames.AuthenticationMethodReference,
                AuthenticationMethodReferences.MultiFactor)
        ];

        if (scopeId is not null)
        {
            claims.Add(new Claim(GmaClaimNames.ScopeId, scopeId));
        }

        JwtSecurityToken token = new(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            notBefore: nowUtc.UtcDateTime,
            expires: nowUtc.AddMinutes(15).UtcDateTime,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private IConfiguration CreatePersistenceConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = provider,
                ["ConnectionStrings:SqlServer"] = provider == "SqlServer" ? providerConnectionString : string.Empty,
                ["ConnectionStrings:PostgreSql"] = provider == "PostgreSql" ? providerConnectionString : string.Empty,
            })
            .Build();

    private sealed class DisabledTenantContext : IAuthScopeContext
    {
        public static readonly DisabledTenantContext Instance = new();

        public bool IsEnabled => false;
        public string? ScopeId => null;
        public bool TryRestoreScope(string? scopeId) => true;
    }
}
