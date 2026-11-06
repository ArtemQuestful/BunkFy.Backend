namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.External;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Application.Events;
using Gma.Framework.Domain;
using Gma.Framework.Messaging;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentOutcomeHandlerTests
{
    [Fact]
    public async Task Confirmation_applies_complete_target_and_finishes_durable_operation()
    {
        Guid currentUnitId = Guid.NewGuid();
        Guid targetUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = Command(context, targetUnitId);
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Reservation.ClearDomainEvents();
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentConfirmedHandler handler = CreateConfirmedHandler(context);

        await handler.HandleAsync(new InventoryAllocationAmendmentConfirmedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            inventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            command.Arrival,
            command.Departure,
            command.InventoryUnitIds,
            allocationVersion: 2), CancellationToken.None);

        Assert.Equal(command.Arrival, context.Reservation.Arrival);
        Assert.Equal(command.Departure, context.Reservation.Departure);
        Assert.Equal(command.ExpectedArrivalTime, context.Reservation.ExpectedArrivalTime);
        Assert.Equal(command.ExpectedDepartureTime, context.Reservation.ExpectedDepartureTime);
        Assert.Equal([targetUnitId], context.Reservation.RequestedUnits.Select(item => item.InventoryUnitId));
        Assert.Equal(2, context.Reservation.DetailsRevision);
        Assert.Null(context.Reservation.PendingAllocationAmendmentId);
        Assert.Equal(1, context.Inventory.AllocationApplyCount);
        ReservationStayAmendmentOperation operation = Assert.Single(context.StayOperations.Items);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Applied, operation.Outcome);
        Assert.Equal(2, operation.ResultingAllocationVersion);
        Assert.Equal(context.Reservation.DetailsRevision, operation.ResultingDetailsRevision);
        Assert.Equal(context.Reservation.Version, operation.ResultingReservationVersion);
        Assert.Empty(context.Reservation.DomainEvents);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new InventoryAllocationAmendmentConfirmedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                command.Arrival,
                command.Departure,
                command.InventoryUnitIds,
                allocationVersion: 3),
            CancellationToken.None));
        Assert.Equal(1, context.Inventory.AllocationApplyCount);
    }

    [Fact]
    public async Task Delayed_exact_confirmation_uses_the_operation_result_after_a_later_amendment()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        InventoryAllocationAmendmentConfirmedHandler handler = CreateConfirmedHandler(context);
        AmendReservationStayCommand firstCommand = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            firstCommand,
            CancellationToken.None)).IsSuccess);
        ReservationStayAmendmentOperation firstOperation = Assert.Single(context.StayOperations.Items);
        Guid firstInventoryRequestId = firstOperation.InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentConfirmedIntegrationEvent firstOutcome = new(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            firstInventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            firstCommand.Arrival,
            firstCommand.Departure,
            firstCommand.InventoryUnitIds,
            allocationVersion: 2);
        await handler.HandleAsync(firstOutcome, CancellationToken.None);

        AmendReservationStayCommand secondCommand = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            secondCommand,
            CancellationToken.None)).IsSuccess);
        ReservationStayAmendmentOperation secondOperation = context.StayOperations.Items.Single(
            operation => operation.Id == secondCommand.OperationId);
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        await handler.HandleAsync(new InventoryAllocationAmendmentConfirmedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            secondOperation.InventoryRequestId!.Value,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            secondCommand.Arrival,
            secondCommand.Departure,
            secondCommand.InventoryUnitIds,
            allocationVersion: 3), CancellationToken.None);

        await handler.HandleAsync(new InventoryAllocationAmendmentConfirmedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            firstInventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            firstCommand.Arrival,
            firstCommand.Departure,
            firstCommand.InventoryUnitIds,
            allocationVersion: 2), CancellationToken.None);

        Assert.Equal(3, context.Reservation.AllocationVersion);
        Assert.Equal(2, context.Inventory.AllocationApplyCount);
        Assert.Equal(2, firstOperation.ResultingAllocationVersion);
        Assert.Equal(3, secondOperation.ResultingAllocationVersion);
    }

    [Fact]
    public async Task Rejection_keeps_all_current_stay_fields_and_finishes_durable_operation()
    {
        Guid currentUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        DateOnly currentArrival = context.Reservation.Arrival;
        DateOnly currentDeparture = context.Reservation.Departure;
        TimeOnly? currentArrivalTime = context.Reservation.ExpectedArrivalTime;
        TimeOnly? currentDepartureTime = context.Reservation.ExpectedDepartureTime;
        long currentDetailsRevision = context.Reservation.DetailsRevision;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentRejectedHandler handler = new(
            context.Mutations,
            context.StayOperations,
            CreateExternalCoordinator(context),
            context.Clock);

        await handler.HandleAsync(new InventoryAllocationAmendmentRejectedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            inventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            InventoryAllocationRejectionReason.AllocationConflict), CancellationToken.None);

        Assert.Equal(currentArrival, context.Reservation.Arrival);
        Assert.Equal(currentDeparture, context.Reservation.Departure);
        Assert.Equal(currentArrivalTime, context.Reservation.ExpectedArrivalTime);
        Assert.Equal(currentDepartureTime, context.Reservation.ExpectedDepartureTime);
        Assert.Equal([currentUnitId], context.Reservation.RequestedUnits.Select(item => item.InventoryUnitId));
        Assert.Equal(currentDetailsRevision, context.Reservation.DetailsRevision);
        Assert.Null(context.Reservation.PendingAllocationAmendmentId);
        ReservationStayAmendmentOperation operation = Assert.Single(context.StayOperations.Items);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Rejected, operation.Outcome);
        Assert.Equal((int)InventoryAllocationRejectionReason.AllocationConflict, operation.RejectionCode);
        Assert.Equal(context.Reservation.Version, operation.ResultingReservationVersion);
    }

    [Fact]
    public async Task Mismatched_confirmation_never_completes_reservation_or_operation()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentConfirmedHandler handler = CreateConfirmedHandler(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new InventoryAllocationAmendmentConfirmedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                command.Arrival,
                command.Departure.AddDays(1),
                command.InventoryUnitIds,
                allocationVersion: 2),
            CancellationToken.None));

        Assert.Equal(command.OperationId, context.Reservation.PendingAllocationAmendmentId);
        Assert.Equal(0, context.Inventory.AllocationApplyCount);
        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.Pending,
            Assert.Single(context.StayOperations.Items).Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Confirmation_rejects_a_nonexact_resulting_allocation_version(
        long allocationVersion)
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateConfirmedHandler(context).HandleAsync(
            new InventoryAllocationAmendmentConfirmedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                command.Arrival,
                command.Departure,
                command.InventoryUnitIds,
                allocationVersion),
            CancellationToken.None));

        Assert.Equal(1, context.Reservation.AllocationVersion);
        Assert.Equal(command.OperationId, context.Reservation.PendingAllocationAmendmentId);
        Assert.Equal(0, context.Inventory.AllocationApplyCount);
        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.Pending,
            Assert.Single(context.StayOperations.Items).Outcome);
    }

    [Fact]
    public async Task Expected_time_only_confirmation_keeps_the_current_allocation_version()
    {
        Guid currentUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = new(
            Guid.NewGuid(),
            context.Reservation.PropertyId,
            context.Reservation.Id,
            context.Reservation.Arrival,
            context.Reservation.Departure,
            new TimeOnly(16, 0),
            new TimeOnly(9, 0),
            [currentUnitId],
            context.Reservation.DetailsRevision,
            "user:operator-a");
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);

        await CreateConfirmedHandler(context).HandleAsync(
            new InventoryAllocationAmendmentConfirmedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                command.Arrival,
                command.Departure,
                command.InventoryUnitIds,
                allocationVersion: 1),
            CancellationToken.None);

        Assert.Equal(1, context.Reservation.AllocationVersion);
        Assert.Equal(command.ExpectedArrivalTime, context.Reservation.ExpectedArrivalTime);
        Assert.Equal(command.ExpectedDepartureTime, context.Reservation.ExpectedDepartureTime);
        Assert.Equal(1, context.Inventory.AllocationApplyCount);
        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.Applied,
            Assert.Single(context.StayOperations.Items).Outcome);
        Assert.Equal(
            1,
            Assert.Single(context.StayOperations.Items).ResultingAllocationVersion);
    }

    [Fact]
    public async Task Applied_operation_rejects_a_late_contradictory_rejection()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentConfirmedHandler confirmedHandler = CreateConfirmedHandler(context);
        await confirmedHandler.HandleAsync(new InventoryAllocationAmendmentConfirmedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            inventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            command.Arrival,
            command.Departure,
            command.InventoryUnitIds,
            allocationVersion: 2), CancellationToken.None);

        InventoryAllocationAmendmentRejectedHandler rejectedHandler = new(
            context.Mutations,
            context.StayOperations,
            CreateExternalCoordinator(context),
            context.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rejectedHandler.HandleAsync(
            new InventoryAllocationAmendmentRejectedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                InventoryAllocationRejectionReason.AllocationConflict),
            CancellationToken.None));

        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.Applied,
            Assert.Single(context.StayOperations.Items).Outcome);
    }

    [Fact]
    public async Task Rejected_operation_rejects_a_late_contradictory_confirmation()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        InventoryAllocationAmendmentRejectedHandler rejectedHandler = new(
            context.Mutations,
            context.StayOperations,
            CreateExternalCoordinator(context),
            context.Clock);
        await rejectedHandler.HandleAsync(new InventoryAllocationAmendmentRejectedIntegrationEvent(
            Guid.NewGuid(),
            context.Reservation.ScopeId,
            context.Clock.UtcNow,
            inventoryRequestId,
            context.Reservation.AllocationId!.Value,
            context.Reservation.Id,
            context.Reservation.PropertyId,
            InventoryAllocationRejectionReason.AllocationConflict), CancellationToken.None);

        InventoryAllocationAmendmentConfirmedHandler confirmedHandler = CreateConfirmedHandler(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => confirmedHandler.HandleAsync(
            new InventoryAllocationAmendmentConfirmedIntegrationEvent(
                Guid.NewGuid(),
                context.Reservation.ScopeId,
                context.Clock.UtcNow,
                inventoryRequestId,
                context.Reservation.AllocationId!.Value,
                context.Reservation.Id,
                context.Reservation.PropertyId,
                command.Arrival,
                command.Departure,
                command.InventoryUnitIds,
                allocationVersion: 2),
            CancellationToken.None));

        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.Rejected,
            Assert.Single(context.StayOperations.Items).Outcome);
        Assert.Equal(0, context.Inventory.AllocationApplyCount);
    }

    private static AmendReservationStayCommand Command(
        ReservationStayAmendmentApplicationTestContext context,
        Guid targetUnitId) => new(
            Guid.NewGuid(),
            context.Reservation.PropertyId,
            context.Reservation.Id,
            context.Reservation.Arrival.AddDays(1),
            context.Reservation.Departure.AddDays(2),
            new TimeOnly(16, 0),
            new TimeOnly(9, 0),
            [targetUnitId],
            context.Reservation.DetailsRevision,
            "user:operator-a");

    private static ExternalReservationOperationCoordinator CreateExternalCoordinator(
        ReservationStayAmendmentApplicationTestContext context) => new(
            new NoOpExternalOperationRepository(),
            new NoOpOutboxRegistry(),
            context.Clock,
            context.Ids);

    private static InventoryAllocationAmendmentConfirmedHandler CreateConfirmedHandler(
        ReservationStayAmendmentApplicationTestContext context) => new(
            context.Mutations,
            context.Inventory,
            context.StayOperations,
            CreateExternalCoordinator(context),
            new ReservationInboxDomainEventDispatcher(new NoOpDomainEventDispatcher()),
            context.Clock,
            context.Ids);

    private sealed class NoOpExternalOperationRepository
        : IReservationExternalOperationRepository
    {
        public Task<ReservationExternalOperationRecord?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult<ReservationExternalOperationRecord?>(null);

        public Task AddAsync(
            ReservationExternalOperationRecord operation,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpOutboxRegistry : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => new NoOpOutboxWriter(moduleName);
    }

    private sealed class NoOpOutboxWriter(string moduleName) : IOutboxWriter
    {
        public string ModuleName { get; } = moduleName;

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent => Task.CompletedTask;
    }

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
