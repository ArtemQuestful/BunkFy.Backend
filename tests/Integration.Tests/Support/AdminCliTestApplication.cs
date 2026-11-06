namespace Integration.Tests.Support;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using BunkFy.Host.AdminCli.Security;
using BunkFy.Modules.Ingestion.AdminCli;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Properties.AdminCli;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.AdminCli;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Inventory.AdminCli;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Parsers.ReservationMail;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Caching.Cqrs;
using Gma.Framework.Cqrs;
using Gma.Framework.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tenancy;
using Gma.Framework.Tenancy.Caching;
using Gma.Framework.Tenancy.Messaging.Infrastructure;
using Gma.Modules.AccessControl.AdminCli;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Administration.AdminCli;
using Gma.Modules.Administration.Persistence;
using Gma.Modules.Administration.Persistence.Entities;
using Gma.Modules.Auth.AdminCli;
using Gma.Modules.Auth.Application;
using Gma.Modules.Auth.Application.Commands;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Domain.Errors;
using Gma.Modules.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

internal sealed class AdminCliTestApplication : IAsyncDisposable
{
    private static readonly SemaphoreSlim ConsoleCaptureGate = new(1, 1);
    private readonly IHost host;
    private readonly RootCommand rootCommand;

    public AdminCliTestApplication(
        string provider,
        string connectionString,
        bool includeIngestion = false,
        bool includeProperties = false,
        bool includeReservations = false,
        ISystemClock? systemClock = null,
        bool includeInventory = false)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Environment.EnvironmentName = "Integration";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["ConnectionStrings:SqlServer"] = provider == "SqlServer" ? connectionString : string.Empty,
            ["ConnectionStrings:PostgreSql"] = provider == "PostgreSql" ? connectionString : string.Empty,
            ["Tenancy:Enabled"] = "true",
            ["AccessControl:Bootstrap:AllowWhenAssignmentsExist"] = "false",
            ["Auth:RefreshTokenLifetimeDays"] = "30",
            ["Auth:RefreshTokens:Pepper"] = AuthTestConfiguration.RefreshTokenPepper,
            ["Auth:Jwt:Issuer"] = "BunkFy",
            ["Auth:Jwt:Audience"] = "BunkFy",
            ["Auth:Jwt:SigningKey"] = "integration-test-signing-key-change-me-000000000000000000",
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Caching:Enabled"] = "false"
        });

        builder.Services.AddGmaAdministrationCli();
        builder.Services.AddBunkFyAdminCliResourceScopes();
        builder.AddCachingCqrs();
        builder.AddGmaInfrastructure();
        builder.AddTenantCaching();
        builder.AddMessagingInfrastructure();
        builder.AddTenantAwareMessaging();
        builder.AddAdminModule<AdministrationAdminCliModule>();
        builder.AddAdminModule<AccessControlAdminCliModule>();
        builder.AddAdminModule<AuthAdminCliModule>();
        if (includeIngestion || includeProperties || includeReservations || includeInventory)
        {
            builder.AddWorkspacesTerminationAdmissionPersistence();
        }

        if (includeProperties || includeReservations)
        {
            builder.Services.AddBunkFyAdminCliResourceScopes();
        }

        if (includeReservations)
        {
            builder.AddAdminModule<ReservationsAdminCliModule>();
        }

        if (includeInventory)
        {
            builder.AddAdminModule<InventoryAdminCliModule>();
        }

        if (includeIngestion)
        {
            builder.Services.AddReservationMailParserDescriptor();
            builder.AddAdminModule<IngestionAdminCliModule>();
        }

        if (includeProperties)
        {
            builder.AddAdminModule<PropertiesAdminCliModule>();
        }

        if (systemClock is not null)
        {
            builder.Services.RemoveAll<ISystemClock>();
            builder.Services.AddSingleton(systemClock);
        }

        this.host = builder.Build();
        this.host.Services.ValidateAdminCliStartup();
        this.rootCommand = this.host.Services.CreateAdminRootCommand();
    }

    public async Task MigrateAsync()
    {
        using IServiceScope scope = this.host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminDbContext>().Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<AccessControlDbContext>().Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync().ConfigureAwait(false);
        WorkspacesDbContext? workspaces = scope.ServiceProvider.GetService<WorkspacesDbContext>();
        if (workspaces is not null)
        {
            await workspaces.Database.MigrateAsync().ConfigureAwait(false);
        }

        ReservationsDbContext? reservations = scope.ServiceProvider.GetService<ReservationsDbContext>();
        if (reservations is not null)
        {
            await reservations.Database.MigrateAsync().ConfigureAwait(false);
        }

        InventoryDbContext? inventory = scope.ServiceProvider.GetService<InventoryDbContext>();
        if (inventory is not null)
        {
            _ = scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>();
            await inventory.Database.MigrateAsync().ConfigureAwait(false);
        }

        IngestionDbContext? ingestion = scope.ServiceProvider.GetService<IngestionDbContext>();
        if (ingestion is not null)
        {
            await ingestion.Database.MigrateAsync().ConfigureAwait(false);
        }

        PropertiesDbContext? properties = scope.ServiceProvider.GetService<PropertiesDbContext>();
        if (properties is not null)
        {
            await properties.Database.MigrateAsync().ConfigureAwait(false);
        }
    }

    public async Task<AdminCliResult> ExecuteAsync(params string[] args)
    {
        await ConsoleCaptureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            using ConcurrentTextWriter output = new();
            using ConcurrentTextWriter error = new();

            Console.SetOut(output);
            Console.SetError(error);

            int exitCode;
            try
            {
                ParseResult parseResult = this.rootCommand.Parse(args);
                exitCode = await parseResult
                    .InvokeAsync(
                        new InvocationConfiguration
                        {
                            EnableDefaultExceptionHandler = false
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            return new AdminCliResult(
                exitCode,
                output.Snapshot(),
                error.Snapshot());
        }
        finally
        {
            ConsoleCaptureGate.Release();
        }
    }

    public async Task<Result<AuthTokensResponse>> LoginAsync(string tenantId, string username, string password)
    {
        using IServiceScope scope = this.host.Services.CreateScope();
        ITenantContextAccessor tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenantContext.SetTenant(tenantId);
        IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();

        Result<PrimaryAuthenticationResult> result = await dispatcher
            .SendAsync(new LoginMemberCommand(username, password), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Failure<AuthTokensResponse>(result.Error);
        }

        return result.Value.Tokens is { } tokens
            ? Result.Success(tokens)
            : Result.Failure<AuthTokensResponse>(AuthApplicationErrors.MultiFactorChallengeInvalid);
    }

    public async Task<int> CountAuditEntriesContainingAsync(string value)
    {
        using IServiceScope scope = this.host.Services.CreateScope();
        AdminDbContext dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();

        return await dbContext.AuditEntries
            .CountAsync(entry =>
                entry.ActorId.Contains(value) ||
                entry.Operation.Contains(value) ||
                entry.Permission.Contains(value) ||
                (entry.ErrorCode != null && entry.ErrorCode.Contains(value)))
            .ConfigureAwait(false);
    }

    public async Task<int> CountAuditEntriesAsync(
        string operation,
        string? errorCode = null,
        string? actorId = null)
    {
        using IServiceScope scope = this.host.Services.CreateScope();
        AdminDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AdminDbContext>();
        IQueryable<AdminAuditEntry> query = dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.Operation == operation);
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            query = query.Where(entry => entry.ErrorCode == errorCode);
        }

        if (!string.IsNullOrWhiteSpace(actorId))
        {
            query = query.Where(entry => entry.ActorId == actorId);
        }

        return await query.CountAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        this.host.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record AdminCliResult(int ExitCode, string Output, string Error)
{
    public Guid GetCreatedMemberId()
    {
        Match match = Regex.Match(this.Output, @"Created member '(?<id>[0-9a-fA-F-]{36})'");
        Xunit.Assert.True(match.Success, this.Output);
        return Guid.Parse(match.Groups["id"].Value);
    }

    public string GetGeneratedPassword()
    {
        Match match = Regex.Match(this.Output, @"Generated password: (?<password>\S+)");
        Xunit.Assert.True(match.Success, this.Output);
        return match.Groups["password"].Value;
    }
}
