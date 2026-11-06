namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Errors;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentLifecycleStateTests
{
    [Theory]
    [InlineData(ReservationState.PendingAllocation)]
    [InlineData(ReservationState.AllocationRejected)]
    [InlineData(ReservationState.CancellationPending)]
    [InlineData(ReservationState.Cancelled)]
    [InlineData(ReservationState.CheckedIn)]
    [InlineData(ReservationState.NoShowPending)]
    [InlineData(ReservationState.NoShow)]
    [InlineData(ReservationState.CheckoutPending)]
    [InlineData(ReservationState.CheckedOut)]
    public void Every_nonconfirmed_lifecycle_state_rejects_a_stay_amendment(
        ReservationState state)
    {
        Reservation reservation = CreateInState(state);

        Result<ReservationDetailsChangeOutcome> result =
            reservation.BeginAllocationAmendment(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new string('a', Reservation.RequestFingerprintLength),
                Arrival.AddDays(1),
                Departure.AddDays(1),
                [Guid.NewGuid()],
                reservation.PrimaryGuestName,
                reservation.Email,
                reservation.Phone,
                reservation.GuestCount,
                reservation.Notes,
                reservation.DetailsRevision,
                ReservationDetailsChangeOrigin.Staff,
                "user:operator",
                adapterConnectionId: null,
                externalOperationId: null,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now.AddMinutes(10));

        Assert.Equal(state, reservation.Status);
        Assert.Equal(ReservationsDomainErrors.AllocationAmendmentInvalid, result.Error);
        Assert.Null(reservation.PendingAllocationAmendmentId);
    }

    private static Reservation CreateInState(ReservationState target)
    {
        Guid unitId = Guid.NewGuid();
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Arrival,
            Departure,
            [unitId],
            "Ada Guest",
            "ada@example.test",
            null,
            1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now).Value;
        if (target == ReservationState.PendingAllocation)
        {
            return reservation;
        }

        if (target == ReservationState.AllocationRejected)
        {
            Assert.True(reservation.RejectAllocation(
                reservation.AllocationRequestId,
                ReservationAllocationRejection.AllocationConflict,
                Guid.NewGuid(),
                Now.AddMinutes(1)).IsSuccess);
            return reservation;
        }

        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Guid releaseRequestId = Guid.NewGuid();
        switch (target)
        {
            case ReservationState.CancellationPending:
            case ReservationState.Cancelled:
                Assert.True(reservation.RequestCancellation(
                    reservation.Version,
                    releaseRequestId,
                    Guid.NewGuid(),
                    Now.AddMinutes(2),
                    "user:operator").IsSuccess);
                break;
            case ReservationState.CheckedIn:
            case ReservationState.CheckoutPending:
            case ReservationState.CheckedOut:
                Assert.True(reservation.CheckIn(
                    reservation.Version,
                    Arrival,
                    "user:operator",
                    Guid.NewGuid(),
                    Now.AddMinutes(2)).IsSuccess);
                if (target is ReservationState.CheckoutPending or ReservationState.CheckedOut)
                {
                    Assert.True(reservation.RequestCheckout(
                        reservation.Version,
                        Arrival,
                        "user:operator",
                        releaseRequestId,
                        Guid.NewGuid(),
                        Now.AddMinutes(3)).IsSuccess);
                }

                break;
            case ReservationState.NoShowPending:
            case ReservationState.NoShow:
                Assert.True(reservation.RequestNoShow(
                    reservation.Version,
                    Arrival,
                    "user:operator",
                    releaseRequestId,
                    Guid.NewGuid(),
                    Now.AddMinutes(2)).IsSuccess);
                break;
            case ReservationState.PendingAllocation:
            case ReservationState.Confirmed:
            case ReservationState.AllocationRejected:
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        if (target is ReservationState.Cancelled or
            ReservationState.NoShow or ReservationState.CheckedOut)
        {
            Assert.True(reservation.CompleteAllocationRelease(
                releaseRequestId,
                Guid.NewGuid(),
                Now.AddMinutes(4)).IsSuccess);
        }

        return reservation;
    }

    private static readonly DateOnly Arrival = new(2026, 8, 20);
    private static readonly DateOnly Departure = new(2026, 8, 23);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}
