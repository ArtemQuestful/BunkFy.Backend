namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ReservationStayAmendmentDispatcherIntegrationTests
{
    private const string TenantId =
        "aa000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        12,
        12,
        0,
        0,
        TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Desired_stay_dispatch_commits_one_correlated_graph_and_exact_replay_is_silent()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_stay_dispatch_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        await using ServiceProvider services = CreateProvider(connectionString);
        Guid propertyId = Guid.NewGuid();
        Guid sharedOperationId =
            Guid.Parse("bb000000-0000-0000-0000-000000000001");
        SeededReservation first = CreateConfirmedReservation(
            propertyId,
            "First Guest");
        SeededReservation second = CreateConfirmedReservation(
            propertyId,
            "Second Guest");
        await MigrateAndSeedAsync(
            services,
            propertyId,
            first,
            second).ConfigureAwait(false);

        AmendReservationStayCommand firstCommand = CreateCommand(
            sharedOperationId,
            propertyId,
            first);
        AmendReservationStayCommand secondCommand = CreateCommand(
            sharedOperationId,
            propertyId,
            second);

        Result<ReservationStayAmendmentReceiptDto> firstResult =
            await SendAsync(services, firstCommand).ConfigureAwait(false);
        Result<ReservationStayAmendmentReceiptDto> secondResult =
            await SendAsync(services, secondCommand).ConfigureAwait(false);

        Assert.True(firstResult.IsSuccess, firstResult.Error.Code);
        Assert.True(secondResult.IsSuccess, secondResult.Error.Code);
        Assert.Equal(sharedOperationId, firstResult.Value.OperationId);
        Assert.Equal(sharedOperationId, secondResult.Value.OperationId);
        Assert.Equal(
            ReservationStayAmendmentOutcome.Pending,
            firstResult.Value.Outcome);
        Assert.Equal(
            ReservationStayAmendmentOutcome.Pending,
            secondResult.Value.Outcome);

        CommittedGraph firstGraph = await ReadCommittedGraphAsync(
            connectionString,
            first.ReservationId,
            sharedOperationId).ConfigureAwait(false);
        CommittedGraph secondGraph = await ReadCommittedGraphAsync(
            connectionString,
            second.ReservationId,
            sharedOperationId).ConfigureAwait(false);
        AssertCommittedGraph(firstCommand, firstGraph);
        AssertCommittedGraph(secondCommand, secondGraph);
        Assert.NotEqual(sharedOperationId, firstGraph.InventoryRequestId);
        Assert.NotEqual(sharedOperationId, secondGraph.InventoryRequestId);
        Assert.NotEqual(
            firstGraph.InventoryRequestId,
            secondGraph.InventoryRequestId);

        OutboxRequest[] requests = await ReadOutboxRequestsAsync(
            connectionString).ConfigureAwait(false);
        Assert.Equal(2, requests.Length);
        AssertOutboxRequest(firstCommand, firstGraph, requests);
        AssertOutboxRequest(secondCommand, secondGraph, requests);
        Assert.Equal(
            2,
            await CountAsync(
                connectionString,
                "reservations.management_operations",
                sharedOperationId).ConfigureAwait(false));
        Assert.Equal(
            2,
            await CountAsync(
                connectionString,
                "reservations.stay_amendment_operations",
                sharedOperationId).ConfigureAwait(false));

        Result<ReservationStayAmendmentReceiptDto> firstReplay =
            await SendAsync(services, firstCommand).ConfigureAwait(false);
        Result<ReservationStayAmendmentReceiptDto> secondReplay =
            await SendAsync(services, secondCommand).ConfigureAwait(false);

        Assert.True(firstReplay.IsSuccess, firstReplay.Error.Code);
        Assert.True(secondReplay.IsSuccess, secondReplay.Error.Code);
        Assert.Equal(
            JsonSerializer.Serialize(firstResult.Value),
            JsonSerializer.Serialize(firstReplay.Value));
        Assert.Equal(
            JsonSerializer.Serialize(secondResult.Value),
            JsonSerializer.Serialize(secondReplay.Value));
        Assert.Equal(
            requests.Select(request => request.MessageId).Order().ToArray(),
            (await ReadOutboxRequestsAsync(connectionString)
                    .ConfigureAwait(false))
                .Select(request => request.MessageId)
                .Order()
                .ToArray());
        Assert.Equal(
            firstGraph,
            await ReadCommittedGraphAsync(
                connectionString,
                first.ReservationId,
                sharedOperationId).ConfigureAwait(false));
        Assert.Equal(
            secondGraph,
            await ReadCommittedGraphAsync(
                connectionString,
                second.ReservationId,
                sharedOperationId).ConfigureAwait(false));
    }

    private static void AssertCommittedGraph(
        AmendReservationStayCommand command,
        CommittedGraph graph)
    {
        Assert.Equal(command.OperationId, graph.PendingOperationId);
        Assert.Equal(
            graph.InventoryRequestId,
            graph.PendingInventoryRequestId);
        Assert.Equal(command.Departure, graph.PendingDeparture);
        Assert.Equal(7, graph.ManagementKind);
        Assert.Equal(
            command.ExpectedDetailsRevision,
            graph.ManagementExpectedDetailsRevision);
        Assert.Equal(64, graph.ManagementFingerprint.Length);
        Assert.Equal(
            graph.ManagementFingerprint,
            graph.OperationFingerprint);
        Assert.Equal(2, graph.RequestSchemaVersion);
        Assert.Equal(command.Departure, graph.TargetDeparture);
        Assert.Equal(1, graph.Outcome);
        Assert.Equal(1, graph.OperationVersion);
        Assert.Equal(command.ActorId, graph.RequestedBy);
        Assert.Equal(graph.ReservationXmin, graph.ManagementXmin);
        Assert.Equal(graph.ReservationXmin, graph.OperationXmin);
    }

    private static void AssertOutboxRequest(
        AmendReservationStayCommand command,
        CommittedGraph graph,
        IReadOnlyCollection<OutboxRequest> requests)
    {
        OutboxRequest request = Assert.Single(
            requests,
            candidate => candidate.InventoryRequestId ==
                graph.InventoryRequestId);
        Assert.Equal(
            typeof(InventoryAllocationAmendmentRequestedIntegrationEvent)
                .FullName,
            request.EventType);
        Assert.Equal(command.ReservationId, request.ReservationId);
        Assert.Equal(command.PropertyId, request.PropertyId);
        Assert.Equal(command.Arrival, request.Arrival);
        Assert.Equal(command.Departure, request.Departure);
        Assert.Equal(
            command.InventoryUnitIds.ToArray(),
            request.InventoryUnitIds.ToArray());
        Assert.Equal(graph.ReservationXmin, request.Xmin);
    }

    private static AmendReservationStayCommand CreateCommand(
        Guid operationId,
        Guid propertyId,
        SeededReservation seeded) => new(
            operationId,
            propertyId,
            seeded.ReservationId,
            seeded.Arrival,
            seeded.Departure.AddDays(1),
            seeded.ExpectedArrivalTime,
            seeded.ExpectedDepartureTime,
            [seeded.InventoryUnitId],
            seeded.DetailsRevision,
            "user:stay-dispatcher");

    private static SeededReservation CreateConfirmedReservation(
        Guid propertyId,
        string guestName)
    {
        Guid reservationId = Guid.NewGuid();
        Guid inventoryUnitId = Guid.NewGuid();
        DateOnly arrival = new(2026, 9, 1);
        DateOnly departure = new(2026, 9, 3);
        TimeOnly expectedArrivalTime = new(15, 0);
        TimeOnly expectedDepartureTime = new(11, 0);
        Reservation reservation = Reservation.Create(
            reservationId,
            TenantId,
            propertyId,
            Guid.NewGuid(),
            arrival,
            departure,
            [inventoryUnitId],
            guestName,
            "guest@example.test",
            null,
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: "user:seed",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now.AddHours(-1),
            expectedArrivalTime,
            expectedDepartureTime).Value;
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now.AddMinutes(-59)).IsSuccess);
        reservation.ClearDomainEvents();
        return new(
            reservation,
            reservationId,
            inventoryUnitId,
            arrival,
            departure,
            expectedArrivalTime,
            expectedDepartureTime,
            reservation.DetailsRevision);
    }

    private static async Task MigrateAndSeedAsync(
        ServiceProvider services,
        Guid propertyId,
        params SeededReservation[] reservations)
    {
        using IServiceScope scope = services.CreateScope();
        ReservationsDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);

        foreach (SeededReservation seeded in reservations)
        {
            dbContext.Reservations.Add(seeded.Reservation);
            dbContext.ProcessingRestrictionProjections.Add(
                ReservationProcessingRestrictionProjection.Create(
                    TenantId,
                    propertyId,
                    seeded.ReservationId,
                    ReservationProcessingRestrictionContract.CurrentVersion,
                    Now.AddHours(-1)).Value);
            dbContext.InventoryUnitProjections.Add(
                ReservationInventoryUnitProjection.Create(
                    new ReservationInventoryUnitWriteModel(
                        TenantId,
                        seeded.InventoryUnitId,
                        propertyId,
                        Guid.NewGuid(),
                        BedId: null,
                        InventoryUnitKind.Room,
                        $"Room {seeded.ReservationId:N}",
                        IsTopologyActive: true,
                        IsSellable: true,
                        ConfigurationVersion: 1,
                        UnitVersion: 1)));
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        foreach (SeededReservation seeded in reservations)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO reservations.reservation_operation_locks
                        ("Id", "ScopeId", "ReservationId", "Revision")
                    VALUES
                        ({Guid.NewGuid()}, {TenantId},
                         {seeded.ReservationId}, 1)
                    """).ConfigureAwait(false);
        }
    }

    private static async Task<Result<ReservationStayAmendmentReceiptDto>>
        SendAsync(
            ServiceProvider services,
            AmendReservationStayCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<CommittedGraph> ReadCommittedGraphAsync(
        string connectionString,
        Guid reservationId,
        Guid operationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new("""
            SELECT
                reservation."PendingAllocationAmendmentId",
                reservation."PendingInventoryAmendmentRequestId",
                reservation."PendingDeparture",
                reservation.xmin::text::bigint,
                management."Kind",
                management."ExpectedDetailsRevision",
                management."RequestFingerprint",
                management.xmin::text::bigint,
                operation."InventoryRequestId",
                operation."RequestSchemaVersion",
                operation."RequestFingerprint",
                operation."TargetDeparture",
                operation."Outcome",
                operation."OperationVersion",
                operation."RequestedBy",
                operation.xmin::text::bigint
            FROM reservations.reservations reservation
            JOIN reservations.management_operations management
              ON management."ScopeId" = reservation."ScopeId"
             AND management."ReservationId" = reservation."Id"
             AND management."Id" = @operationId
            JOIN reservations.stay_amendment_operations operation
              ON operation."ScopeId" = management."ScopeId"
             AND operation."ReservationId" = management."ReservationId"
             AND operation."Id" = management."Id"
            WHERE reservation."ScopeId" = @tenantId
              AND reservation."Id" = @reservationId
            """, connection);
        command.Parameters.AddWithValue("tenantId", TenantId);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue("operationId", operationId);
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync()
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));
        CommittedGraph graph = new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetFieldValue<DateOnly>(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetGuid(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.GetFieldValue<DateOnly>(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetInt64(15));
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
        return graph;
    }

    private static async Task<OutboxRequest[]> ReadOutboxRequestsAsync(
        string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new("""
            SELECT "Id", "EventType", "Payload", xmin::text::bigint
            FROM reservations.outbox_messages
            WHERE "ScopeId" = @tenantId
              AND "EventType" = @eventType
            ORDER BY "Id"
            """, connection);
        command.Parameters.AddWithValue("tenantId", TenantId);
        command.Parameters.AddWithValue(
            "eventType",
            typeof(InventoryAllocationAmendmentRequestedIntegrationEvent)
                .FullName!);
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync()
            .ConfigureAwait(false);
        List<OutboxRequest> requests = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            Guid messageId = reader.GetGuid(0);
            string eventType = reader.GetString(1);
            string payload = reader.GetString(2);
            long xmin = reader.GetInt64(3);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            requests.Add(new(
                messageId,
                eventType,
                root.GetProperty("amendmentRequestId").GetGuid(),
                root.GetProperty("reservationId").GetGuid(),
                root.GetProperty("propertyId").GetGuid(),
                DateOnly.Parse(
                    root.GetProperty("arrival").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                DateOnly.Parse(
                    root.GetProperty("departure").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                root.GetProperty("inventoryUnitIds")
                    .EnumerateArray()
                    .Select(item => item.GetGuid())
                    .ToArray(),
                xmin));
        }

        return requests.ToArray();
    }

    private static async Task<long> CountAsync(
        string connectionString,
        string qualifiedTable,
        Guid operationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"SELECT COUNT(*) FROM {qualifiedTable} " +
            "WHERE \"ScopeId\" = @tenantId AND \"Id\" = @operationId",
            connection);
        command.Parameters.AddWithValue("tenantId", TenantId);
        command.Parameters.AddWithValue("operationId", operationId);
        object? value = await command.ExecuteScalarAsync()
            .ConfigureAwait(false);
        return Convert.ToInt64(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext());
        builder.Services.AddSingleton<ISystemClock>(new TestClock());
        builder.Services.AddSingleton<IIdGenerator, SequentialIdGenerator>();
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddReservationsApplication();
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record SeededReservation(
        Reservation Reservation,
        Guid ReservationId,
        Guid InventoryUnitId,
        DateOnly Arrival,
        DateOnly Departure,
        TimeOnly ExpectedArrivalTime,
        TimeOnly ExpectedDepartureTime,
        long DetailsRevision);

    private sealed record CommittedGraph(
        Guid PendingOperationId,
        Guid PendingInventoryRequestId,
        DateOnly PendingDeparture,
        long ReservationXmin,
        int ManagementKind,
        long ManagementExpectedDetailsRevision,
        string ManagementFingerprint,
        long ManagementXmin,
        Guid InventoryRequestId,
        int RequestSchemaVersion,
        string OperationFingerprint,
        DateOnly TargetDeparture,
        int Outcome,
        long OperationVersion,
        string RequestedBy,
        long OperationXmin);

    private sealed record OutboxRequest(
        Guid MessageId,
        string EventType,
        Guid InventoryRequestId,
        Guid ReservationId,
        Guid PropertyId,
        DateOnly Arrival,
        DateOnly Departure,
        IReadOnlyCollection<Guid> InventoryUnitIds,
        long Xmin);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private int next;

        public Guid NewId()
        {
            byte[] bytes = new byte[16];
            BitConverter.GetBytes(Interlocked.Increment(ref this.next))
                .CopyTo(bytes, 0);
            bytes[^1] = 1;
            return new Guid(bytes);
        }
    }
}
