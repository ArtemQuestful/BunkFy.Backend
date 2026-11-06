namespace Integration.Tests;

using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestManagementOperationIntegrationTests
{
    private const string TenantId =
        "a8000000-0000-0000-0000-000000000001";
    private const string OtherTenantId =
        "a8000000-0000-0000-0000-000000000002";
    private static readonly Guid PropertyId =
        Guid.Parse("78000000-0000-0000-0000-000000000001");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Update_and_archive_replay_through_one_atomic_guest_boundary()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guest_management_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        MutableScopeContext scopeContext = new(TenantId);
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            scopeContext);
        await MigrateAsync(services).ConfigureAwait(false);
        await SeedPropertyAsync(services).ConfigureAwait(false);

        Guid guestId = Guid.NewGuid();
        Result<GuestMutationReceiptDto> created = await SendAsync(
            services,
            new CreateGuestProfileCommand(
                guestId,
                PropertyId,
                "Maya Chen",
                "Maya Q. Chen",
                "maya@example.test",
                "+44 20 1234 5678",
                new DateOnly(1990, 2, 3),
                "GB",
                "en-GB",
                "Prefers a lower bunk.",
                "user:operator", LanguageTags: ["en-GB", "fr"])).ConfigureAwait(false);
        Assert.True(created.IsSuccess, created.Error.Code);

        Guid updateOperationId = Guid.NewGuid();
        UpdateGuestProfileCommand update = new(
            updateOperationId,
            PropertyId,
            guestId,
            "Maya Chen Updated",
            "Maya Q. Chen",
            "MAYA@EXAMPLE.TEST",
            "+44 20 1234 5678",
            new DateOnly(1990, 2, 3),
            "gb",
            "en-GB",
            "Updated once.",
            created.Value.Version,
            "user:operator", ["fr", "en-GB", "unknown-١"]);
        Result<GuestMutationReceiptDto>[] concurrentUpdates =
            await Task.WhenAll(
                SendAsync(services, update),
                SendAsync(services, update)).ConfigureAwait(false);
        Assert.All(
            concurrentUpdates,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentUpdates[0].Value, concurrentUpdates[1].Value);
        GuestMutationReceiptDto updated = concurrentUpdates[0].Value;

        Result<GuestMutationReceiptDto> conflictingReuse = await SendAsync(
            services,
            update with { DisplayName = "Different reuse" })
            .ConfigureAwait(false);
        Assert.True(conflictingReuse.IsFailure);
        Assert.Equal(
            GuestsApplicationErrors.ManagementOperationConflict.Code,
            conflictingReuse.Error.Code);

        Result<GuestMutationReceiptDto> staleMutation = await SendAsync(
            services,
            update with
            {
                OperationId = Guid.NewGuid(),
                DisplayName = "Stale mutation"
            }).ConfigureAwait(false);
        Assert.True(staleMutation.IsFailure);
        Assert.Equal(
            GuestsApplicationErrors.VersionConflict.Code,
            staleMutation.Error.Code);

        Guid archiveOperationId = Guid.NewGuid();
        ArchiveGuestProfileCommand archive = new(
            archiveOperationId,
            PropertyId,
            guestId,
            updated.Version,
            "user:operator");
        Result<GuestMutationReceiptDto> archived = await SendAsync(
            services,
            archive).ConfigureAwait(false);
        Result<GuestMutationReceiptDto> archiveReplay = await SendAsync(
            services,
            archive).ConfigureAwait(false);
        Assert.True(archived.IsSuccess, archived.Error.Code);
        Assert.True(archiveReplay.IsSuccess, archiveReplay.Error.Code);
        Assert.Equal(archived.Value, archiveReplay.Value);

        await VerifyOwnerStateAsync(
            services,
            guestId,
            updateOperationId,
            archiveOperationId,
            archived.Value).ConfigureAwait(false);

        scopeContext.ScopeId = OtherTenantId;
        using IServiceScope isolatedScope = services.CreateScope();
        GuestsDbContext isolated = isolatedScope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        Assert.False(await isolated.GuestProfiles.AnyAsync()
            .ConfigureAwait(false));
        Assert.False(await isolated.ManagementOperations.AnyAsync()
            .ConfigureAwait(false));
    }

    private static async Task VerifyOwnerStateAsync(
        ServiceProvider services,
        Guid guestId,
        Guid updateOperationId,
        Guid archiveOperationId,
        GuestMutationReceiptDto archived)
    {
        using IServiceScope scope = services.CreateScope();
        GuestsDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        GuestProfile profile = await dbContext.GuestProfiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == guestId)
            .ConfigureAwait(false);
        GuestManagementOperation[] operations = await dbContext
            .ManagementOperations
            .AsNoTracking()
            .OrderBy(operation => operation.CompletedAtUtc)
            .ThenBy(operation => operation.Id)
            .ToArrayAsync()
            .ConfigureAwait(false);

        Assert.Equal(GuestProfileState.Archived, profile.Status);
        Assert.Equal(["en-gb", "fr", "unknown-١"], profile.LanguageTags);
        Assert.Equal(archived.Version, profile.Version);
        Assert.Equal(2, operations.Length);
        Assert.Contains(
            operations,
            operation => operation.Id == updateOperationId &&
                operation.Kind == GuestManagementOperationKind.Update);
        Assert.Contains(
            operations,
            operation => operation.Id == archiveOperationId &&
                operation.Kind == GuestManagementOperationKind.Archive);
        Assert.Single(
            dbContext.OutboxMessages,
            message => message.EventType.Contains(
                "GuestProfileUpdated",
                StringComparison.Ordinal));
        Assert.Single(
            dbContext.OutboxMessages,
            message => message.EventType.Contains(
                "GuestProfileArchived",
                StringComparison.Ordinal));
    }

    private static async Task<Result<GuestMutationReceiptDto>> SendAsync(
        ServiceProvider services,
        ICommand<GuestMutationReceiptDto> command)
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
        await scope.ServiceProvider.GetRequiredService<GuestsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static async Task SeedPropertyAsync(ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        GuestsDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync()
            .ConfigureAwait(false);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(),
            TenantId,
            DateTimeOffset.UtcNow,
            PropertyId,
            "Management House",
            "management-house",
            "UTC",
            PropertyStatus.Active,
            1);
        await ResolveHandler<PropertyCreatedIntegrationEvent>(
                scope.ServiceProvider)
            .HandleAsync(propertyCreated, CancellationToken.None)
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            GuestsModuleMetadata.Name,
            TenantId,
            PropertyId,
            propertyVersion: 2).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static IIntegrationEventHandler<TEvent> ResolveHandler<TEvent>(
        IServiceProvider services)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == GuestsModuleMetadata.Name &&
                item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services
            .GetRequiredService(subscription.HandlerType);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        MutableScopeContext scopeContext)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(scopeContext);
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddGuestsApplication();
        builder.AddGuestsPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class MutableScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId { get; set; } = scopeId;
    }
}
