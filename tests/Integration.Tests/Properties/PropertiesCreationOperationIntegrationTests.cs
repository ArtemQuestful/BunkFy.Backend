namespace Integration.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertiesCreationOperationIntegrationTests
{
    private const string TenantId =
        "aa000000-0000-0000-0000-000000000001";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Concurrent_creation_retries_converge_and_replay_honors_admission()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_creation_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        await MigrateAsync(services).ConfigureAwait(false);
        Guid operationId = Guid.NewGuid();
        CreatePropertyCommand command = new(
            operationId,
            "Harbour House",
            "harbour-house",
            "UTC",
            "system:integration-test");

        Result<PropertyMutationReceiptDto>[] concurrent =
            await Task.WhenAll(
                SendAsync(services, command),
                SendAsync(services, command with
                {
                    Name = "  Harbour House  ",
                    Code = " HARBOUR-HOUSE ",
                    TimeZoneId = " UTC "
                })).ConfigureAwait(false);

        Assert.All(
            concurrent,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrent[0].Value, concurrent[1].Value);
        Assert.Equal(operationId, concurrent[0].Value.PropertyId);

        Result<PropertyMutationReceiptDto> conflictingReuse =
            await SendAsync(
                services,
                command with { Name = "Different House" })
                .ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.CreationOperationConflict,
            conflictingReuse.Error);

        using (IServiceScope verificationScope = services.CreateScope())
        {
            PropertiesDbContext dbContext = verificationScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            Assert.Equal(
                1,
                await dbContext.Properties.CountAsync(property =>
                    property.Id == operationId).ConfigureAwait(false));
            Assert.Equal(
                1,
                await dbContext.OutboxMessages.CountAsync()
                    .ConfigureAwait(false));
            long lockCount = await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM properties.property_operation_locks
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {operationId}
                    """)
                .SingleAsync()
                .ConfigureAwait(false);
            Assert.Equal(1, lockCount);
        }

        await CloseTenantLifecycleAsync(
            services,
            Guid.NewGuid()).ConfigureAwait(false);
        Result<PropertyMutationReceiptDto> closedReplay = await SendAsync(
            services,
            command).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);
    }

    private static async Task<Result<TResponse>> SendAsync<TResponse>(
        ServiceProvider services,
        ICommand<TResponse> command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task MigrateAsync(ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task CloseTenantLifecycleAsync(
        ServiceProvider services,
        Guid destroyOperationId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.tenant_revisions
                SET "Revision" = "Revision" + 1,
                    "LifecycleStatus" = 2,
                    "DestroyOperationId" = {destroyOperationId},
                    "DestroyRequestSha256" = {new string('a', 64)},
                    "DestroyStartedAtUtc" = {new DateTimeOffset(
                        2026,
                        8,
                        7,
                        18,
                        30,
                        0,
                        TimeSpan.Zero)}
                WHERE "ScopeId" = {TenantId}
                """).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddPropertiesApplication();
        builder.AddPropertiesPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
