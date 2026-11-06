namespace BunkFy.Modules.Stations.Tests;

using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Xunit;

public sealed partial class StationFirstJobServiceTests
{
    [Theory]
    [InlineData(StationAuthorityKind.LinkedStation)]
    [InlineData(StationAuthorityKind.StationOnly)]
    public async Task Checkout_only_identity_has_departures_without_check_in_authority(StationAuthorityKind kind)
    {
        using var f = new Fixture(kind);
        f.CheckInGrant = null;
        f.CheckOutGrant = new(9, false);
        f.JobStatus[WorkspaceStaffStationAction.ReservationCheckIn] = WorkspaceStaffStationObservationStatus.PrerequisitesNotObserved;
        var jobs = await f.Service.ReadJobsAsync(Credential, f.Actor);
        Assert.Equal(StationReservationState.Denied, jobs.CheckIn);
        Assert.Equal(StationReservationState.Ready, jobs.CheckOut);
        Assert.Equal(StationSessionState.Active, (await f.ViewAsync()).Runtime.State);
        var page = await f.Service.ListDeparturesAsync(Credential, f.Actor);
        Assert.Equal(StationReservationState.Ready, page.State);
        Assert.Equal(LocalDate, page.PropertyLocalDate);
        Assert.Single(page.Items);
        Assert.Empty(f.Operations.ListCalls);
        var result = await f.Service.CheckOutAsync(Credential, f.Actor, Operation, Id(100), 12);
        Assert.Equal(StationReservationState.Applied, result.State);
        Assert.Equal(StationCheckOutOutcomeState.ReleasePending, result.Checkout);
        var provenance = Assert.Single(f.Departures.Dispatched).Provenance;
        Assert.Equal(kind == StationAuthorityKind.StationOnly ? 9L : null, provenance.JobGrantRevision);
        Assert.Equal(f.Actor.ActorSessionId, provenance.ActorSessionId);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task No_task_actor_remains_active_and_neither_guest_owner_is_read()
    {
        using var f = new Fixture(StationAuthorityKind.StationOnly);
        f.CheckInGrant = null;
        f.CheckOutGrant = null;
        Assert.Equal(StationSessionState.Active, (await f.ViewAsync()).Runtime.State);
        var jobs = await f.Service.ReadJobsAsync(Credential, f.Actor);
        Assert.Equal(StationReservationState.Denied, jobs.CheckIn);
        Assert.Equal(StationReservationState.Denied, jobs.CheckOut);
        Assert.Equal(StationReservationState.Denied, (await f.Service.ListDeparturesAsync(Credential, f.Actor)).State);
        Assert.Empty(f.Departures.ListCalls);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("departure-list")]
    [InlineData("departure-prepare")]
    [InlineData("labels")]
    [InlineData("departure-dispatch")]
    public async Task Checkout_grant_revision_is_rechecked_after_each_owner_callback(string boundary)
    {
        using var f = new Fixture(StationAuthorityKind.StationOnly);
        f.CheckOutGrant = new(9, false);
        f.OnEvent = name =>
        {
            if (name == boundary)
            { f.CheckOutGrant = new(10, false); }
        };
        if (boundary == "departure-list")
        {
            var page = await f.Service.ListDeparturesAsync(Credential, f.Actor);
            Assert.Equal(StationReservationState.Denied, page.State);
            Assert.Empty(page.Items);
            Assert.Null(page.PropertyId);
        }
        else
        {
            var result = await f.Service.CheckOutAsync(Credential, f.Actor, Operation, Id(100), 12);
            Assert.Equal(StationReservationState.Denied, result.State);
            Assert.Null(result.Receipt);
            Assert.Null(result.Checkout);
        }
        Assert.Equal(boundary == "departure-dispatch" ? 1 : 0, f.Departures.Dispatched.Count);
    }

    [Fact]
    public async Task Unrelated_check_in_revocation_does_not_invalidate_checkout()
    {
        using var f = new Fixture(StationAuthorityKind.StationOnly);
        f.CheckOutGrant = new(9, false);
        f.OnEvent = name =>
        {
            if (name == "departure-prepare")
            { f.CheckInGrant = new(4, true); }
        };
        Assert.Equal(StationReservationState.Applied,
            (await f.Service.CheckOutAsync(Credential, f.Actor, Operation, Id(100), 12)).State);
        Assert.Equal(StationSessionState.Active, (await f.ViewAsync()).Runtime.State);
    }

    [Fact]
    public async Task Checkout_replay_skips_new_labels_and_preserves_original_date()
    {
        using var f = new Fixture();
        f.Departures.Replay = true;
        var result = await f.Service.CheckOutAsync(Credential, f.Actor, Operation, Id(100), 12);
        Assert.Equal(StationCheckOutOutcomeState.ReleasePending, result.Checkout);
        Assert.Empty(f.Inventory.Batches);
        Assert.Equal(LocalDate.AddDays(-1), Assert.Single(f.Departures.Dispatched).Date);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Missing_neutral_registration_evidence_never_reads_either_job(long revision)
    {
        using var f = new Fixture();
        f.Facts = f.Facts! with { RegistrationRevision = revision };
        Assert.Equal(StationSessionState.StateChanged, (await f.ViewAsync()).Runtime.State);
        Assert.Equal(StationReservationState.Denied, (await f.ListAsync()).State);
        Assert.Equal(StationReservationState.Denied, (await f.Service.ListDeparturesAsync(Credential, f.Actor)).State);
        AssertNoProtectedCalls(f);
        Assert.Empty(f.Departures.ListCalls);
    }

    private sealed class DepartureOwner(Fixture fixture) : IStationDepartureOperations
    {
        public bool Replay { get; set; }
        public List<(Guid PropertyId, DateOnly Date)> ListCalls { get; } = [];
        public List<OperationCall> Dispatched { get; } = [];
        private static StationDueDeparture Departure()
        {
            var arrival = Arrival(100, [Unit(1)]);
            return new(arrival.ReservationId, arrival.PrimaryGuestName, arrival.Arrival, LocalDate,
                arrival.Arrival, arrival.ExpectedVersion, arrival.AllocationId, arrival.AllocationVersion,
                arrival.Units, StationDepartureState.Due);
        }
        public Task<StationDueDeparturePage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
            StationDepartureCursor? after, CancellationToken cancellationToken = default)
        {
            this.ListCalls.Add((propertyId, localDate));
            fixture.Record("departure-list");
            return Task.FromResult(new StationDueDeparturePage(StationReservationState.Ready, [Departure()]));
        }
        public Task<StationCheckOutPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
            long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
        {
            fixture.Record("departure-prepare");
            return Task.FromResult(this.Replay
                ? new StationCheckOutPreparation(StationReservationState.Ready, LocalDate.AddDays(-1), Replay: true)
                : new(StationReservationState.Ready, LocalDate, Departure()));
        }
        public Task<StationCheckOutResult> CheckOutAsync(Guid propertyId, Guid reservationId, Guid operationId,
            long expectedVersion, StationCheckOutPreparation preparation, StationCheckInProvenance provenance,
            CancellationToken cancellationToken = default)
        {
            this.Dispatched.Add(new(propertyId, reservationId, operationId, expectedVersion, preparation.BusinessDate, provenance));
            fixture.Record("departure-dispatch");
            return Task.FromResult(new StationCheckOutResult(StationReservationState.Applied,
                new(reservationId, propertyId, ReservationStatus.CheckoutPending, 2, expectedVersion + 1),
                StationCheckOutOutcomeState.ReleasePending));
        }
    }
}
