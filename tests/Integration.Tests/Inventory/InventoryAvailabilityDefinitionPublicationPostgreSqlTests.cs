namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Contracts.Stations;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>Actual allocation handler, owner transaction/outbox and Reservations consumers; no broker or HTTP claim.</summary>
public sealed class InventoryAvailabilityDefinitionPublicationPostgreSqlTests
{
    private const string Tenant = "ac000000-0000-0000-0000-000000000001";
    private static readonly Guid Property = Guid.Parse("ac000000-0000-0000-0000-000000000002");
    private static readonly Guid Room = Guid.Parse("ac000000-0000-0000-0000-000000000003");
    private static readonly Guid Bed = Guid.Parse("ac000000-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 9, 28);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Accepted_allocation_publishes_current_definition_atomically_and_duplicate_is_silent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        InventoryAllocationRequestedIntegrationEvent request = await SeedAsync(fixture.Services);

        await DeliverAsync(fixture.Services, InventoryModuleMetadata.Name, request);

        InventoryUnitDefinitionChangedIntegrationEvent definition = await ReadOutboxAsync<InventoryUnitDefinitionChangedIntegrationEvent>(fixture.Services);
        InventoryAllocationConfirmedIntegrationEvent confirmed = await ReadOutboxAsync<InventoryAllocationConfirmedIntegrationEvent>(fixture.Services);
        Assert.Equal(4, definition.UnitVersion);
        Assert.Equal(2, definition.ConfigurationVersion);
        Assert.Equal(Bed, definition.InventoryUnitId);
        Assert.True(definition.IsTopologyActive);
        Assert.True(definition.IsSellable);
        await AssertSameTransactionAsync(fixture.ConnectionString, confirmed.AllocationId);

        // Allocation confirmation may arrive first: the station's exact version guard must still block this gap.
        await DeliverAsync(fixture.Services, ReservationsModuleMetadata.Name, confirmed);
        (StationDueArrivalPage pending, StationInventoryLabels live) = await ReadStationInputsAsync(fixture.Services);
        Assert.Equal(StationReservationState.Ready, pending.State);
        Assert.Equal(StationInventoryLabelsState.Current, live.State);
        Assert.Equal(3, Assert.Single(Assert.Single(pending.Items).Units).UnitVersion);
        Assert.Equal(4, Assert.Single(live.Items).UnitVersion);

        await DeliverAsync(fixture.Services, ReservationsModuleMetadata.Name, definition);
        (StationDueArrivalPage converged, StationInventoryLabels current) = await ReadStationInputsAsync(fixture.Services);
        StationAllocationUnit projected = Assert.Single(Assert.Single(converged.Items).Units);
        StationInventoryLabel owner = Assert.Single(current.Items);
        Assert.Equal(StationReservationState.Ready, converged.State);
        Assert.Equal(StationInventoryLabelsState.Current, current.State);
        Assert.Equal(owner.UnitVersion, projected.UnitVersion);
        Assert.Equal(owner.ConfigurationVersion, projected.ConfigurationVersion);
        Assert.Equal(owner.RoomId, projected.RoomId);
        Assert.Equal(owner.BedId, projected.BedId);

        await DeliverAsync(fixture.Services, InventoryModuleMetadata.Name, request);
        await DeliverAsync(fixture.Services, ReservationsModuleMetadata.Name, definition);
        using IServiceScope check = fixture.Services.CreateScope();
        InventoryDbContext inventory = check.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(4, (await inventory.InventoryUnits.AsNoTracking().SingleAsync(unit => unit.Id == Bed)).AvailabilityMutationVersion);
        Assert.Equal(3, (await inventory.RoomConfigurations.AsNoTracking().SingleAsync()).AvailabilityMutationVersion);
        Assert.Equal(2, await inventory.OutboxMessages.CountAsync());
        Assert.Single(await inventory.Allocations.AsNoTracking().ToArrayAsync());
        await AssertRetirementOrderingAsync(fixture.Services);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Definition_outbox_failure_rolls_back_allocation_and_availability_versions()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        InventoryAllocationRequestedIntegrationEvent request = await SeedAsync(fixture.Services);
        await using (NpgsqlConnection connection = new(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand reject = new("""
                ALTER TABLE inventory.outbox_messages
                ADD CONSTRAINT "CK_fixture_reject_unit_definition"
                CHECK ("EventType" <> 'BunkFy.Modules.Inventory.Contracts.InventoryUnitDefinitionChangedIntegrationEvent')
                """, connection);
            await reject.ExecuteNonQueryAsync();
        }

        DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
            DeliverAsync(fixture.Services, InventoryModuleMetadata.Name, request));
        PostgresException databaseFailure = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("CK_fixture_reject_unit_definition", databaseFailure.ConstraintName);

        using IServiceScope check = fixture.Services.CreateScope();
        InventoryDbContext inventory = check.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(3, (await inventory.InventoryUnits.AsNoTracking().SingleAsync(unit => unit.Id == Bed)).AvailabilityMutationVersion);
        Assert.Equal(2, (await inventory.RoomConfigurations.AsNoTracking().SingleAsync()).AvailabilityMutationVersion);
        Assert.Empty(await inventory.Allocations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await inventory.OutboxMessages.AsNoTracking().ToArrayAsync());
        ReservationsDbContext reservations = check.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        Assert.Equal(3, (await reservations.InventoryUnitProjections.AsNoTracking().SingleAsync()).UnitVersion);
        Assert.Equal(ReservationState.PendingAllocation, (await reservations.Reservations.AsNoTracking().SingleAsync()).Status);
    }

    private static async Task AssertRetirementOrderingAsync(ServiceProvider services)
    {
        using (IServiceScope scope = services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>().SendAsync(
                new RequestRoomRetirementCommand(Guid.NewGuid(), Property, Room, true, "Synthetic closure", "user:operator"));
            Assert.True(result.IsSuccess, result.Error.Code);
        }

        InventoryUnitDefinitionChangedIntegrationEvent[] definitions;
        using (IServiceScope scope = services.CreateScope())
        {
            InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            string[] payloads = await inventory.OutboxMessages.AsNoTracking()
                .Where(message => message.EventType == typeof(InventoryUnitDefinitionChangedIntegrationEvent).FullName)
                .Select(message => message.Payload).ToArrayAsync();
            definitions = payloads.Select(payload => Assert.IsType<InventoryUnitDefinitionChangedIntegrationEvent>(
                    JsonSerializer.Deserialize<InventoryUnitDefinitionChangedIntegrationEvent>(payload, JsonSerializerOptions.Web)))
                .OrderBy(item => item.UnitVersion).ToArray();
        }
        Assert.Equal([4L, 5L, 6L], definitions.Select(item => item.UnitVersion));
        Assert.False(definitions[1].IsSellable);
        Assert.False(definitions[2].IsSellable);
        // The later PublishRoomAsync remains authoritative; older/repeated delivery cannot resurrect sales.
        await DeliverAsync(services, ReservationsModuleMetadata.Name, definitions[2]);
        await DeliverAsync(services, ReservationsModuleMetadata.Name, definitions[1]);
        await DeliverAsync(services, ReservationsModuleMetadata.Name, definitions[0]);
        using IServiceScope verify = services.CreateScope();
        ReservationsDbContext reservations = verify.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        ReservationInventoryUnitProjection projected = await reservations.InventoryUnitProjections.AsNoTracking().SingleAsync();
        Assert.Equal(6, projected.UnitVersion);
        Assert.False(projected.IsSellable);
        StationDueArrivalPage arrivals = await verify.ServiceProvider.GetRequiredService<IStationDueArrivalRepository>()
            .ListAsync(Property, Day, 25, null, CancellationToken.None);
        Assert.Equal(StationReservationState.Incomplete, arrivals.State);
        Assert.Empty(arrivals.Items);
    }

    private static async Task<InventoryAllocationRequestedIntegrationEvent> SeedAsync(ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(Property, Tenant);
        property.Apply("Synthetic property", "synthetic", "UTC", PropertyStatus.Active, 1);
        InventoryRoomTopology room = InventoryRoomTopology.Create(Room, Tenant, Property);
        room.Apply(Property, "Synthetic room", null, null, RoomStatus.Active, 1);
        InventoryBedTopology bed = InventoryBedTopology.Create(Bed, Tenant, Property, Room);
        bed.Apply(Property, Room, "Synthetic bed", BedStatus.Active, 1);
        InventoryUnit unit = InventoryUnit.CreateBed(Bed, Tenant, Property, Room);
        unit.Apply(Property, Room, Bed, InventoryUnitKind.Bed, "Synthetic bed", true, 1);
        unit.TouchAvailability();
        unit.TouchAvailability();
        RoomInventoryConfiguration configuration = RoomInventoryConfiguration.Create(Room, Tenant, Property, Now).Value;
        Assert.True(configuration.Configure(RoomSalesMode.BedLevel, 1, Guid.NewGuid(), Now).IsSuccess);
        configuration.ClearDomainEvents();
        inventory.AddRange(property, room, bed, unit, configuration);
        await inventory.SaveChangesAsync();

        Reservation reservation = Reservation.Create(Guid.NewGuid(), Tenant, Property, Guid.NewGuid(),
            Day, Day.AddDays(3), [Bed], "Synthetic guest", "synthetic@example.test", null, 1,
            ReservationSource.Direct, null, null, null, Guid.NewGuid(), Guid.NewGuid(), ReservationDetailsChangeOrigin.Staff,
            "user:synthetic-seed", null, null, Guid.NewGuid(), Now, new TimeOnly(15, 0), new TimeOnly(11, 0)).Value;
        ReservationDetailsChangedDomainEvent history = Assert.Single(reservation.DomainEvents.OfType<ReservationDetailsChangedDomainEvent>());
        reservation.ClearDomainEvents();
        await scope.ServiceProvider.GetRequiredService<IReservationRepository>().AddAsync(reservation, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IReservationDetailsHistoryWriter>().AppendAsync(history, CancellationToken.None);
        ReservationsDbContext reservations = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        reservations.InventoryUnitProjections.Add(ReservationInventoryUnitProjection.Create(new(
            Tenant, Bed, Property, Room, Bed, InventoryUnitKind.Bed, "Synthetic bed", true, true, 2, 3)));
        await reservations.SaveChangesAsync();
        return new(Guid.NewGuid(), Tenant, Now, reservation.Id, reservation.AllocationRequestId, Property, Day, Day.AddDays(3), [Bed]);
    }

    private static async Task DeliverAsync<TEvent>(ServiceProvider services, string module, TEvent message)
        where TEvent : IIntegrationEvent
    {
        using IServiceScope scope = services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;
        IntegrationEventSubscription subscription = Assert.Single(provider.GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions, item => item.ConsumerModule == module && item.EventType == typeof(TEvent));
        var handler = (IIntegrationEventHandler<TEvent>)provider.GetRequiredService(subscription.HandlerType);
        IRollbackResettableUnitOfWork work = Assert.IsType<IRollbackResettableUnitOfWork>(
            Assert.Single(provider.GetServices<IUnitOfWork>(), item => item.ModuleName == module), exactMatch: false);
        await work.BeginTransactionAsync();
        try
        {
            await handler.HandleAsync(message, CancellationToken.None);
            await work.SaveChangesAsync();
            await work.CommitTransactionAsync();
        }
        catch
        {
            await work.RollbackTransactionAsync();
            await work.ResetAfterRollbackAsync();
            throw;
        }
    }

    private static async Task<TEvent> ReadOutboxAsync<TEvent>(ServiceProvider services)
        where TEvent : class, IIntegrationEvent
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        string payload = await inventory.OutboxMessages.AsNoTracking()
            .Where(message => message.EventType == typeof(TEvent).FullName)
            .Select(message => message.Payload).SingleAsync();
        return Assert.IsType<TEvent>(JsonSerializer.Deserialize<TEvent>(payload, JsonSerializerOptions.Web));
    }

    private static async Task<(StationDueArrivalPage Arrivals, StationInventoryLabels Labels)> ReadStationInputsAsync(ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        StationDueArrivalPage arrivals = await scope.ServiceProvider.GetRequiredService<IStationDueArrivalRepository>()
            .ListAsync(Property, Day, 25, null, CancellationToken.None);
        StationInventoryLabels labels = await scope.ServiceProvider.GetRequiredService<IStationInventoryLabelReader>()
            .ReadAsync(Property, [Bed], CancellationToken.None);
        return (arrivals, labels);
    }

    private static async Task AssertSameTransactionAsync(string connectionString, Guid allocationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand query = new("""
            SELECT xmin::text::bigint FROM inventory.allocations WHERE "ScopeId" = @scope AND "Id" = @allocation
            UNION ALL SELECT xmin::text::bigint FROM inventory.inventory_units WHERE "ScopeId" = @scope AND "Id" = @unit
            UNION ALL SELECT xmin::text::bigint FROM inventory.room_configurations WHERE "ScopeId" = @scope AND "Id" = @room
            UNION ALL SELECT xmin::text::bigint FROM inventory.outbox_messages WHERE "ScopeId" = @scope
            """, connection);
        query.Parameters.AddWithValue("scope", Tenant);
        query.Parameters.AddWithValue("allocation", allocationId);
        query.Parameters.AddWithValue("unit", Bed);
        query.Parameters.AddWithValue("room", Room);
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
        List<long> transactionIds = [];
        while (await reader.ReadAsync())
        {
            transactionIds.Add(reader.GetInt64(0));
        }
        Assert.Equal(5, transactionIds.Count);
        Assert.Single(transactionIds.Distinct());
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString
        });
        builder.Services.AddSingleton<IScopeContext>(new TestScope());
        builder.Services.AddSingleton<ISystemClock>(new TestClock());
        builder.Services.AddSingleton<IIdGenerator, TestIds>();
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
        builder.Services.AddReservationsApplication();
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class Fixture(PostgreSqlContainer container, ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public string ConnectionString => container.GetConnectionString();

        public static async Task<Fixture> CreateAsync()
        {
            PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithName($"bunkfy-pin-availability-{Guid.NewGuid():N}")
                .WithLabel("bunkfy.test.grant", "PIN-AVAILABILITY-PUBLICATION")
                .WithDatabase("station_availability_publication").Build();
            ServiceProvider? services = null;
            try
            {
                await container.StartAsync();
                services = CreateProvider(container.GetConnectionString());
                using IServiceScope scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>().Database.MigrateAsync();
                return new(container, services);
            }
            catch
            {
                if (services is not null)
                {
                    await services.DisposeAsync();
                }
                await container.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.Services.DisposeAsync();
            }
            finally
            {
                await container.DisposeAsync();
            }
        }
    }

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => Tenant;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
