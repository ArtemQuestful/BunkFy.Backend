namespace Integration.Tests;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Ports;
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

public sealed class PropertiesLifecycleOperationIntegrationTests
{
    private const string TenantId =
        "ac000000-0000-0000-0000-000000000001";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Lifecycle_retries_commit_one_receipt_revision_and_event()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_lifecycle_operation_tests")
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
                "Lifecycle House",
                "lifecycle-house",
                "UTC",
                "system:integration-test")).ConfigureAwait(false);
        Assert.True(created.IsSuccess, created.Error.Code);

        Guid activationOperationId = Guid.NewGuid();
        ActivatePropertyProcessingCommand activation = Activation(
            propertyId,
            activationOperationId,
            expectedVersion: 1);
        Result<PropertyMutationReceiptDto>[] activated =
            await Task.WhenAll(
                SendAsync(services, activation),
                SendAsync(
                    services,
                    activation with
                    {
                        AcceptedAcknowledgements =
                        [.. activation.AcceptedAcknowledgements.Reverse()]
                    })).ConfigureAwait(false);

        Assert.All(
            activated,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(activated[0].Value, activated[1].Value);
        Assert.Equal(2, activated[0].Value.Version);

        Result<PropertyMutationReceiptDto> changedActivationReuse =
            await SendAsync(
                services,
                activation with { DataRegionId = "not-permitted" })
                .ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            changedActivationReuse.Error);

        SuspendPropertyProcessingCommand suspension = new(
            propertyId,
            Guid.NewGuid(),
            true,
            ExpectedVersion: 2,
            "user:owner");
        Result<PropertyMutationReceiptDto>[] suspended =
            await Task.WhenAll(
                SendAsync(services, suspension),
                SendAsync(services, suspension)).ConfigureAwait(false);
        Assert.All(
            suspended,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(suspended[0].Value, suspended[1].Value);
        Assert.Equal(3, suspended[0].Value.Version);

        RetirePropertyCommand retirement = new(
            propertyId,
            Guid.NewGuid(),
            true,
            ExpectedVersion: 3,
            "user:owner");
        Result<PropertyMutationReceiptDto>[] retired = await Task.WhenAll(
            SendAsync(services, retirement),
            SendAsync(services, retirement)).ConfigureAwait(false);
        Assert.All(
            retired,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(retired[0].Value, retired[1].Value);
        Assert.Equal(4, retired[0].Value.Version);

        Result<PropertyMutationReceiptDto> retiredReplay = await SendAsync(
            services,
            retirement).ConfigureAwait(false);
        Assert.True(retiredReplay.IsSuccess, retiredReplay.Error.Code);
        Assert.Equal(retired[0].Value, retiredReplay.Value);

        Guid correctedPropertyId = Guid.NewGuid();
        Assert.True((await SendAsync(
            services,
            new CreatePropertyCommand(
                correctedPropertyId,
                "Corrected House",
                "corrected-house",
                "UTC",
                "system:integration-test")).ConfigureAwait(false)).IsSuccess);
        Guid correctedOperationId = Guid.NewGuid();
        ActivatePropertyProcessingCommand denied = Activation(
            correctedPropertyId,
            correctedOperationId,
            expectedVersion: 1) with
        {
            DataRegionId = "not-permitted"
        };
        Result<PropertyMutationReceiptDto> failed = await SendAsync(
            services,
            denied).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.CountryPolicyDenied(
                CountryPolicyDecisionReason.DataRegionNotPermitted),
            failed.Error);
        Assert.Equal(
            0,
            await CountOperationsAsync(services, correctedPropertyId)
                .ConfigureAwait(false));

        Result<PropertyMutationReceiptDto> corrected = await SendAsync(
            services,
            denied with { DataRegionId = "integration-region" })
            .ConfigureAwait(false);
        Assert.True(corrected.IsSuccess, corrected.Error.Code);
        Assert.Equal(2, corrected.Value.Version);

        using (IServiceScope verificationScope = services.CreateScope())
        {
            PropertiesDbContext dbContext = verificationScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            Assert.Equal(
                [
                    (int)PropertyMutationKind.ProcessingActivation,
                    (int)PropertyMutationKind.ProcessingSuspension,
                    (int)PropertyMutationKind.Retirement
                ],
                await LifecycleKindsAsync(dbContext, propertyId)
                    .ConfigureAwait(false));
            Assert.Equal(
                2,
                await dbContext.GovernanceRevisions.CountAsync(
                    revision => revision.PropertyId == propertyId)
                    .ConfigureAwait(false));
            Assert.Equal(
                4,
                await dbContext.Properties
                    .Where(property => property.Id == propertyId)
                    .Select(property => property.Version)
                    .SingleAsync()
                    .ConfigureAwait(false));
            await AssertSingleOutboxEventAsync<PropertyProcessingPolicyActivatedIntegrationEvent>(
                dbContext,
                propertyId).ConfigureAwait(false);
            await AssertSingleOutboxEventAsync<PropertyProcessingSuspendedIntegrationEvent>(
                dbContext,
                propertyId).ConfigureAwait(false);
            await AssertSingleOutboxEventAsync<PropertyRetiredIntegrationEvent>(
                dbContext,
                propertyId).ConfigureAwait(false);
        }

        await CloseTenantLifecycleAsync(
            services,
            Guid.NewGuid()).ConfigureAwait(false);
        Result<PropertyMutationReceiptDto> closedReplay = await SendAsync(
            services,
            retirement).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);
    }

    private static ActivatePropertyProcessingCommand Activation(
        Guid propertyId,
        Guid operationId,
        long expectedVersion) => new(
            propertyId,
            operationId,
            "GB",
            "integration-hostel-baseline",
            1,
            "integration-region",
            "integration-no-transfer",
            "integration-guest-operational",
            1,
            [new("integration-operator-notice", 1)],
            true,
            expectedVersion,
            "user:owner");

    private static async Task AssertSingleOutboxEventAsync<TEvent>(
        PropertiesDbContext dbContext,
        Guid propertyId)
    {
        int count = await dbContext.OutboxMessages.CountAsync(message =>
            EF.Functions.Like(
                message.EventType,
                $"%{typeof(TEvent).Name}%") &&
            message.Payload.Contains(propertyId.ToString()))
            .ConfigureAwait(false);
        Assert.Equal(1, count);
    }

    private static async Task<long> CountOperationsAsync(
        ServiceProvider services,
        Guid propertyId)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>()
            .Database.SqlQuery<long>($"""
                SELECT COUNT(*) AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {propertyId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static Task<List<int>> LifecycleKindsAsync(
        PropertiesDbContext dbContext,
        Guid propertyId) => dbContext.Database.SqlQuery<int>($"""
                SELECT "Kind" AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {propertyId}
                ORDER BY "Kind"
                """)
            .ToListAsync();

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
                        20,
                        0,
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
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
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
