namespace Integration.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
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

public sealed class PropertiesDetailsUpdateOperationIntegrationTests
{
    private const string TenantId =
        "ab000000-0000-0000-0000-000000000001";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Concurrent_updates_replay_once_and_closed_admission_wins()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_update_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        await MigrateAsync(services).ConfigureAwait(false);
        Guid propertyId = Guid.NewGuid();
        Result<PropertyMutationReceiptDto> created = await SendAsync(
            services,
            new CreatePropertyCommand(
                propertyId,
                "Harbour House",
                "harbour-house",
                "UTC",
                "system:integration-test")).ConfigureAwait(false);
        Assert.True(created.IsSuccess, created.Error.Code);

        Guid updateOperationId = Guid.NewGuid();
        UpdatePropertyCommand update = new(
            propertyId,
            updateOperationId,
            "Updated House",
            "updated-house",
            TimeZoneId: null,
            ExpectedVersion: 1);
        Result<PropertyMutationReceiptDto>[] concurrent =
            await Task.WhenAll(
                SendAsync(services, update),
                SendAsync(services, update with
                {
                    Name = "  Updated House  ",
                    Code = " UPDATED-HOUSE "
                })).ConfigureAwait(false);

        Assert.All(
            concurrent,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrent[0].Value, concurrent[1].Value);
        Assert.Equal(2, concurrent[0].Value.Version);

        Result<PropertyMutationReceiptDto> conflictingReuse =
            await SendAsync(
                services,
                update with { Name = "Different House" })
                .ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            conflictingReuse.Error);

        Guid occupiedPropertyId = Guid.NewGuid();
        Result<PropertyMutationReceiptDto> occupiedCreated = await SendAsync(
            services,
            new CreatePropertyCommand(
                occupiedPropertyId,
                "Occupied House",
                "occupied-house",
                "UTC",
                "system:integration-test")).ConfigureAwait(false);
        Assert.True(occupiedCreated.IsSuccess, occupiedCreated.Error.Code);

        Guid correctedOperationId = Guid.NewGuid();
        UpdatePropertyCommand duplicateCode = new(
            propertyId,
            correctedOperationId,
            "Harbour Annex",
            "occupied-house",
            TimeZoneId: null,
            ExpectedVersion: 2);
        Result<PropertyMutationReceiptDto> failed = await SendAsync(
            services,
            duplicateCode).ConfigureAwait(false);
        Assert.Equal(
            PropertiesDomainErrors.PropertyCodeAlreadyExists,
            failed.Error);
        Assert.Equal(
            1,
            await CountOperationsAsync(services, propertyId)
                .ConfigureAwait(false));

        Result<PropertyMutationReceiptDto> corrected = await SendAsync(
            services,
            duplicateCode with { Code = "harbour-annex" })
            .ConfigureAwait(false);
        Assert.True(corrected.IsSuccess, corrected.Error.Code);
        Assert.Equal(3, corrected.Value.Version);

        Guid noChangeOperationId = Guid.NewGuid();
        Result<PropertyMutationReceiptDto> noChange = await SendAsync(
            services,
            new UpdatePropertyCommand(
                propertyId,
                noChangeOperationId,
                " Harbour Annex ",
                " HARBOUR-ANNEX ",
                TimeZoneId: null,
                ExpectedVersion: 3)).ConfigureAwait(false);
        Assert.True(noChange.IsSuccess, noChange.Error.Code);
        Assert.Equal(3, noChange.Value.Version);

        Result<PropertyMutationReceiptDto> immutableReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.True(immutableReplay.IsSuccess, immutableReplay.Error.Code);
        Assert.Equal(2, immutableReplay.Value.Version);

        using (IServiceScope verificationScope = services.CreateScope())
        {
            PropertiesDbContext dbContext = verificationScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            Assert.Equal(
                3,
                await CountOperationsAsync(dbContext, propertyId)
                    .ConfigureAwait(false));
            Assert.Equal(
                2,
                await dbContext.OutboxMessages.CountAsync(message =>
                    EF.Functions.Like(
                        message.EventType,
                        $"%{nameof(PropertyUpdatedIntegrationEvent)}%"))
                    .ConfigureAwait(false));
            Assert.Equal(
                3,
                await dbContext.Properties
                    .Where(property => property.Id == propertyId)
                    .Select(property => property.Version)
                    .SingleAsync()
                    .ConfigureAwait(false));
        }

        await CloseTenantLifecycleAsync(
            services,
            Guid.NewGuid()).ConfigureAwait(false);
        Result<PropertyMutationReceiptDto> closedReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);
    }

    private static async Task<long> CountOperationsAsync(
        ServiceProvider services,
        Guid propertyId)
    {
        using IServiceScope scope = services.CreateScope();
        return await CountOperationsAsync(
                scope.ServiceProvider.GetRequiredService<PropertiesDbContext>(),
                propertyId)
            .ConfigureAwait(false);
    }

    private static Task<long> CountOperationsAsync(
        PropertiesDbContext dbContext,
        Guid propertyId) =>
        dbContext.Database.SqlQuery<long>($"""
                SELECT COUNT(*) AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {propertyId}
                """)
            .SingleAsync();

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
                    "DestroyRequestSha256" = {new string('b', 64)},
                    "DestroyStartedAtUtc" = {new DateTimeOffset(
                        2026,
                        8,
                        7,
                        19,
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
