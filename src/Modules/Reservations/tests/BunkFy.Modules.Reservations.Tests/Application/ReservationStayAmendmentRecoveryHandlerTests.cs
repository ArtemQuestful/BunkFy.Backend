namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentRecoveryHandlerTests
{
    [Fact]
    public async Task Reconcile_is_version_checked_rate_bounded_and_republishes_exact_candidate()
    {
        Guid targetUnitId = Guid.NewGuid();
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand amend = AmendCommand(context, targetUnitId);
        ReservationStayAmendmentReceiptDto pending = (await context.AmendHandler.HandleAsync(
            amend,
            CancellationToken.None)).Value;
        context.Reservation.ClearDomainEvents();
        ReconcileReservationStayAmendmentCommandHandler handler = new(
            context.Mutations,
            context.StayOperations,
            context.Clock,
            context.Ids);

        Result<ReservationStayAmendmentReceiptDto> tooSoon = await handler.HandleAsync(
            ReconcileCommand(context, amend.OperationId, pending.OperationVersion),
            CancellationToken.None);
        Assert.Equal(ReservationsApplicationErrors.StayAmendmentReconcileTooSoon, tooSoon.Error);
        Assert.Empty(context.Reservation.DomainEvents);
        Assert.Equal(pending.OperationVersion, Assert.Single(context.StayOperations.Items).OperationVersion);

        long selectedReservationVersion = context.Reservation.Version;
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(5);
        Result<ReservationStayAmendmentReceiptDto> accepted = await handler.HandleAsync(
            ReconcileCommand(context, amend.OperationId, pending.OperationVersion),
            CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Code);
        Assert.Equal(pending.OperationVersion + 1, accepted.Value.OperationVersion);
        Assert.Equal(1, accepted.Value.ReconciliationCount);
        Assert.Equal(selectedReservationVersion + 1, context.Reservation.Version);
        ReservationAllocationAmendmentRequestedDomainEvent replay =
            Assert.IsType<ReservationAllocationAmendmentRequestedDomainEvent>(
                Assert.Single(context.Reservation.DomainEvents));
        Assert.Equal(
            Assert.Single(context.StayOperations.Items).InventoryRequestId,
            replay.AmendmentRequestId);
        Assert.Equal(amend.Arrival, replay.Arrival);
        Assert.Equal(amend.Departure, replay.Departure);
        Assert.Equal([targetUnitId], replay.InventoryUnitIds);

        context.Reservation.ClearDomainEvents();
        Result<ReservationStayAmendmentReceiptDto> stale = await handler.HandleAsync(
            ReconcileCommand(context, amend.OperationId, pending.OperationVersion),
            CancellationToken.None);
        Assert.Equal(ReservationsApplicationErrors.StayAmendmentOperationVersionConflict, stale.Error);
        Assert.Empty(context.Reservation.DomainEvents);
    }

    [Fact]
    public async Task Exact_status_returns_nullable_target_for_historical_unknown()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        Guid operationId = Guid.NewGuid();
        ReservationStayAmendmentOperation unknown = ReservationStayAmendmentOperation.CreateOutcomeUnknown(
            operationId,
            context.Reservation.ScopeId,
            context.Reservation.PropertyId,
            context.Reservation.Id,
            ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            context.Reservation.DetailsRevision,
            context.Clock.UtcNow).Value;
        context.StayOperations.Items.Add(unknown);
        GetReservationStayAmendmentQueryHandler handler = new(
            context.StayOperations,
            context.Clock);

        Result<ReservationStayAmendmentReceiptDto> result = await handler.HandleAsync(
            new GetReservationStayAmendmentQuery(
                context.Reservation.PropertyId,
                context.Reservation.Id,
                operationId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ReservationStayAmendmentOutcome.OutcomeUnknown, result.Value.Outcome);
        Assert.Null(result.Value.Target);
        Assert.False(result.Value.RecoveryEligible);
        Assert.Null(result.Value.NextRecoveryEligibleAtUtc);
    }

    [Fact]
    public async Task Recovery_list_is_privacy_minimal_and_maps_only_recovery_outcomes()
    {
        ReservationStayAmendmentApplicationTestContext context = new([Guid.NewGuid()]);
        AmendReservationStayCommand amend = AmendCommand(context, Guid.NewGuid());
        Assert.True((await context.AmendHandler.HandleAsync(amend, CancellationToken.None)).IsSuccess);
        ListReservationStayAmendmentRecoveryQueryHandler handler = new(
            context.StayOperations,
            context.Clock);

        Result<ReservationStayAmendmentRecoveryPageDto> result = await handler.HandleAsync(
            new ListReservationStayAmendmentRecoveryQuery(
                context.Reservation.PropertyId,
                Cursor: null,
                PageSize: 20),
            CancellationToken.None);

        ReservationStayAmendmentRecoveryItemDto item = Assert.Single(result.Value.Operations);
        Assert.Equal(amend.OperationId, item.OperationId);
        Assert.Equal(ReservationStayAmendmentOutcome.Pending, item.Outcome);
        Assert.DoesNotContain(
            typeof(ReservationStayAmendmentRecoveryItemDto).GetProperties(),
            property => property.Name.Contains("Guest", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Phone", StringComparison.OrdinalIgnoreCase));
    }

    private static AmendReservationStayCommand AmendCommand(
        ReservationStayAmendmentApplicationTestContext context,
        Guid targetUnitId) => new(
            Guid.NewGuid(),
            context.Reservation.PropertyId,
            context.Reservation.Id,
            context.Reservation.Arrival.AddDays(1),
            context.Reservation.Departure.AddDays(1),
            new TimeOnly(16, 0),
            new TimeOnly(9, 0),
            [targetUnitId],
            context.Reservation.DetailsRevision,
            "user:operator-a");

    private static ReconcileReservationStayAmendmentCommand ReconcileCommand(
        ReservationStayAmendmentApplicationTestContext context,
        Guid operationId,
        long expectedOperationVersion) => new(
            context.Reservation.PropertyId,
            context.Reservation.Id,
            operationId,
            expectedOperationVersion,
            "user:operator-b");
}
