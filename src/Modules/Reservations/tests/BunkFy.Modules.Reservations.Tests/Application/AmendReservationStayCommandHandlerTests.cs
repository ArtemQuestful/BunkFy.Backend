namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class AmendReservationStayCommandHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Date_only_and_unit_only_changes_use_the_same_pending_protocol(
        bool dateOnly)
    {
        Guid currentUnitId = Guid.NewGuid();
        Guid targetUnitId = dateOnly ? currentUnitId : Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            dateOnly ? context.Reservation.Arrival.AddDays(1) : context.Reservation.Arrival,
            dateOnly ? context.Reservation.Departure.AddDays(1) : context.Reservation.Departure,
            context.Reservation.ExpectedArrivalTime,
            context.Reservation.ExpectedDepartureTime,
            [targetUnitId]);

        Result<ReservationStayAmendmentReceiptDto> result =
            await context.AmendHandler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ReservationStayAmendmentOutcome.Pending, result.Value.Outcome);
        Assert.Equal(command.Arrival, context.Reservation.PendingArrival);
        Assert.Equal(command.Departure, context.Reservation.PendingDeparture);
        Assert.Equal(
            [targetUnitId],
            context.Reservation.PendingInventoryUnitIds!
                .Split(',')
                .Select(value => Guid.ParseExact(value, "N")));
    }

    [Fact]
    public async Task Invalid_target_range_is_rejected_without_reserving_an_operation()
    {
        Guid currentUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Departure,
            context.Reservation.Arrival,
            expectedArrivalTime: null,
            expectedDepartureTime: null,
            [currentUnitId]);

        Result<ReservationStayAmendmentReceiptDto> result =
            await context.AmendHandler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(ReservationsApplicationErrors.StayRangeInvalid, result.Error);
        Assert.Empty(context.ManagementOperations.Items);
        Assert.Empty(context.StayOperations.Items);
        Assert.Null(context.Reservation.PendingAllocationAmendmentId);
    }

    [Fact]
    public async Task Combined_change_records_pending_target_while_current_stay_remains_authoritative()
    {
        Guid currentUnitId = Guid.NewGuid();
        Guid targetUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([currentUnitId]);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Arrival.AddDays(1),
            context.Reservation.Departure.AddDays(2),
            new TimeOnly(16, 30),
            new TimeOnly(9, 15),
            [targetUnitId]);

        Result<ReservationStayAmendmentReceiptDto> result = await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ReservationStayAmendmentOutcome.Pending, result.Value.Outcome);
        Assert.Equal(command.Arrival, result.Value.Target!.Arrival);
        Assert.Equal(command.Departure, result.Value.Target.Departure);
        Assert.Equal(command.ExpectedArrivalTime, result.Value.Target.ExpectedArrivalTime);
        Assert.Equal(command.ExpectedDepartureTime, result.Value.Target.ExpectedDepartureTime);
        Assert.Equal([targetUnitId], result.Value.Target.InventoryUnitIds);
        Assert.Equal(ReservationStayAmendmentApplicationTestContext.Arrival, context.Reservation.Arrival);
        Assert.Equal(ReservationStayAmendmentApplicationTestContext.Departure, context.Reservation.Departure);
        Assert.Null(context.Reservation.ExpectedArrivalTime);
        Assert.Null(context.Reservation.ExpectedDepartureTime);
        Assert.Equal([currentUnitId], context.Reservation.RequestedUnits.Select(item => item.InventoryUnitId));
        Assert.Equal(command.Arrival, context.Reservation.PendingArrival);
        Assert.Equal(command.ExpectedArrivalTime, context.Reservation.PendingExpectedArrivalTime);
        ReservationAllocationAmendmentRequestedDomainEvent requested =
            Assert.IsType<ReservationAllocationAmendmentRequestedDomainEvent>(
                Assert.Single(context.Reservation.DomainEvents));
        ReservationManagementOperationRecord journal = Assert.Single(context.ManagementOperations.Items);
        Assert.Equal(ReservationManagementOperationKind.StayAmendment, journal.Kind);
        ReservationStayAmendmentOperation operation = Assert.Single(context.StayOperations.Items);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Pending, operation.Outcome);
        Assert.NotEqual(command.OperationId, operation.InventoryRequestId);
        Assert.Equal(operation.InventoryRequestId, context.Reservation.PendingInventoryAmendmentRequestId);
        Assert.Equal(operation.InventoryRequestId, requested.AmendmentRequestId);
    }

    [Fact]
    public async Task Already_current_desired_state_is_durably_applied_without_inventory_request()
    {
        Guid currentUnitId = Guid.NewGuid();
        TimeOnly arrivalTime = new(15, 0);
        TimeOnly departureTime = new(10, 0);
        ReservationStayAmendmentApplicationTestContext context = new(
            [currentUnitId],
            arrivalTime,
            departureTime);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Arrival,
            context.Reservation.Departure,
            arrivalTime,
            departureTime,
            [currentUnitId]);
        long selectedReservationVersion = context.Reservation.Version;

        Result<ReservationStayAmendmentReceiptDto> first = await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None);
        Result<ReservationStayAmendmentReceiptDto> replay = await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(ReservationStayAmendmentOutcome.Applied, first.Value.Outcome);
        Assert.Equal(context.Reservation.DetailsRevision, first.Value.ResultingDetailsRevision);
        Assert.Equal(context.Reservation.Version, first.Value.ResultingReservationVersion);
        Assert.Equal(selectedReservationVersion + 1, context.Reservation.Version);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value with { Target = null }, replay.Value with { Target = null });
        Guid[] ignoredUnits = [];
        Assert.Equal(
            first.Value.Target! with { InventoryUnitIds = ignoredUnits },
            replay.Value.Target! with { InventoryUnitIds = ignoredUnits });
        Assert.Equal(
            first.Value.Target.InventoryUnitIds,
            replay.Value.Target.InventoryUnitIds);
        Assert.Single(context.ManagementOperations.Items);
        Assert.Single(context.StayOperations.Items);
        Assert.Empty(context.Reservation.DomainEvents);
        Assert.Equal(1, context.Inventory.ValidationCount);
    }

    [Fact]
    public async Task Exact_terminal_retry_returns_stored_rejection_receipt()
    {
        Guid targetUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Arrival,
            context.Reservation.Departure,
            expectedArrivalTime: null,
            expectedDepartureTime: null,
            [targetUnitId]);
        Assert.True((await context.AmendHandler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        context.Reservation.ClearDomainEvents();
        Guid inventoryRequestId = Assert.Single(context.StayOperations.Items).InventoryRequestId!.Value;
        Assert.True(context.Reservation.RejectAllocationAmendment(
            inventoryRequestId,
            context.Reservation.AllocationId!.Value,
            rejectionCode: 2,
            context.Clock.UtcNow.AddMinutes(1)).IsSuccess);
        ReservationStayAmendmentOperation operation = Assert.Single(context.StayOperations.Items);
        Assert.True(operation.MarkRejected(
            rejectionCode: 2,
            context.Reservation.DetailsRevision,
            context.Reservation.Version,
            context.Clock.UtcNow.AddMinutes(1)).IsSuccess);

        Result<ReservationStayAmendmentReceiptDto> replay = await context.AmendHandler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(ReservationStayAmendmentOutcome.Rejected, replay.Value.Outcome);
        Assert.Equal(2, (int?)replay.Value.RejectionReason);
        Assert.Equal(operation.OperationVersion, replay.Value.OperationVersion);
        Assert.Equal(1, context.Inventory.ValidationCount);
        Assert.Empty(context.Reservation.DomainEvents);
    }

    [Fact]
    public async Task Reusing_operation_with_changed_target_conflicts_before_inventory_validation()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand command = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Arrival.AddDays(1),
            context.Reservation.Departure.AddDays(1),
            expectedArrivalTime: null,
            expectedDepartureTime: null,
            [Guid.NewGuid()]);
        Assert.True((await context.AmendHandler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        Result<ReservationStayAmendmentReceiptDto> changed = await context.AmendHandler.HandleAsync(
            command with { Departure = command.Departure.AddDays(1) },
            CancellationToken.None);

        Assert.Equal(ReservationsApplicationErrors.StayAmendmentOperationConflict, changed.Error);
        Assert.Equal(1, context.Inventory.ValidationCount);
        Assert.Single(context.ManagementOperations.Items);
        Assert.Single(context.StayOperations.Items);
    }

    [Fact]
    public async Task Stale_details_revision_and_pending_competitor_fail_closed()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand first = Command(
            context.Reservation,
            Guid.NewGuid(),
            context.Reservation.Arrival.AddDays(1),
            context.Reservation.Departure.AddDays(1),
            expectedArrivalTime: null,
            expectedDepartureTime: null,
            [Guid.NewGuid()]);
        Result<ReservationStayAmendmentReceiptDto> stale = await context.AmendHandler.HandleAsync(
            first with { ExpectedDetailsRevision = first.ExpectedDetailsRevision + 1 },
            CancellationToken.None);
        Assert.Equal(ReservationsApplicationErrors.DetailsRevisionConflict, stale.Error);

        Assert.True((await context.AmendHandler.HandleAsync(first, CancellationToken.None)).IsSuccess);
        Result<ReservationStayAmendmentReceiptDto> competitor = await context.AmendHandler.HandleAsync(
            first with { OperationId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.Equal(ReservationsApplicationErrors.AllocationAmendmentInProgress, competitor.Error);
    }

    private static AmendReservationStayCommand Command(
        Reservation reservation,
        Guid operationId,
        DateOnly arrival,
        DateOnly departure,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime,
        IReadOnlyCollection<Guid> units) => new(
            operationId,
            reservation.PropertyId,
            reservation.Id,
            arrival,
            departure,
            expectedArrivalTime,
            expectedDepartureTime,
            units,
            reservation.DetailsRevision,
            "user:operator-a");
}
