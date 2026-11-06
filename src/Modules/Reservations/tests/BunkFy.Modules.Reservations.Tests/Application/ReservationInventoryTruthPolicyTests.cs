namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationInventoryTruthPolicyTests
{
    [Theory]
    [InlineData(ReservationState.PendingAllocation, false, false)]
    [InlineData(ReservationState.PendingAllocation, true, false)]
    [InlineData(ReservationState.Confirmed, false, false)]
    [InlineData(ReservationState.Confirmed, true, true)]
    [InlineData(ReservationState.AllocationRejected, false, false)]
    [InlineData(ReservationState.AllocationRejected, true, false)]
    [InlineData(ReservationState.CancellationPending, false, false)]
    [InlineData(ReservationState.CancellationPending, true, true)]
    [InlineData(ReservationState.Cancelled, false, false)]
    [InlineData(ReservationState.Cancelled, true, false)]
    [InlineData(ReservationState.CheckedIn, false, false)]
    [InlineData(ReservationState.CheckedIn, true, true)]
    [InlineData(ReservationState.NoShowPending, false, false)]
    [InlineData(ReservationState.NoShowPending, true, true)]
    [InlineData(ReservationState.NoShow, false, false)]
    [InlineData(ReservationState.NoShow, true, false)]
    [InlineData(ReservationState.CheckoutPending, false, false)]
    [InlineData(ReservationState.CheckoutPending, true, true)]
    [InlineData(ReservationState.CheckedOut, false, false)]
    [InlineData(ReservationState.CheckedOut, true, false)]
    public void Hold_truth_requires_a_live_holding_state_and_allocation(
        ReservationState status,
        bool hasAllocation,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReservationInventoryTruthPolicy.HoldsInventory(
                status,
                hasAllocation ? Guid.NewGuid() : null));
    }
}
