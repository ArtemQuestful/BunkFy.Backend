namespace BunkFy.Modules.Reservations.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Stations;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Xunit;

/// <summary>Actual owner orchestration with counted ports; not persistence, provider, transaction or HTTP proof.</summary>
[Trait("Category", "Unit")]
public sealed class StationReservationOperationsTests
{
    private static readonly Guid PropertyId = Id(1);
    private static readonly Guid ReservationId = Id(2);
    private static readonly Guid OperationId = Id(3);
    private static readonly DateOnly Today = new(2026, 9, 27);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly StationCheckInProvenance Provenance = new(
        Id(10), Id(11), Id(12), Id(13), 7, StationReservationAuthority.LinkedStation);

    [Theory]
    [InlineData("prepare")]
    [InlineData("check-in")]
    public async Task New_station_only_calls_without_job_revision_conflict_before_owner_reads(string operation)
    {
        var h = new Harness();
        var provenance = Provenance with { Authority = StationReservationAuthority.StationOnly };
        Assert.Equal(StationReservationState.Conflict, await InvokeAsync(h, operation, PropertyId, provenance: provenance));
        Assert.Empty(h.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Fact]
    public async Task Only_original_legacy_station_only_checkin_coordinates_can_replay_with_live_job_revision()
    {
        var legacy = Provenance with { Authority = StationReservationAuthority.StationOnly };
        var current = legacy with { JobGrantRevision = 3 };
        var h = new Harness { Prior = Prior(), Attribution = new(true, legacy), Target = null };
        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, current);
        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.True(result.Replay);
        Assert.False(StationOperationProvenance.ValidNew(legacy));
        Assert.True(StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckIn, legacy, current));
        Assert.False(StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckOut, legacy, current));
        Assert.False(StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckIn, current, legacy));
        Assert.False(StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckIn, legacy,
            current with { ActorSessionId = Id(99) }));
        Assert.False(StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckIn, legacy,
            current with { Authority = StationReservationAuthority.LinkedStation, JobGrantRevision = null }));
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("prepare", false)]
    [InlineData("check-in", false)]
    [InlineData("list", true)]
    [InlineData("prepare", true)]
    [InlineData("check-in", true)]
    public async Task Unsupported_provider_stops_before_policy_or_protected_reads_even_for_empty_property(
        string operation, bool emptyProperty)
    {
        var h = new Harness { Supported = false };

        var state = await InvokeAsync(h, operation, emptyProperty ? Guid.Empty : PropertyId);

        Assert.Equal(StationReservationState.Unsupported, state);
        Assert.Equal(["provider"], h.Calls);
        Assert.Empty(h.Country.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("prepare")]
    [InlineData("check-in")]
    public async Task Empty_property_is_denied_before_country_or_protected_calls(string operation)
    {
        var h = new Harness();

        Assert.Equal(StationReservationState.Denied, await InvokeAsync(h, operation, Guid.Empty));

        Assert.Equal(["provider"], h.Calls);
        Assert.Empty(h.Country.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("prepare")]
    [InlineData("check-in")]
    public async Task Denied_current_country_policy_stops_before_queue_receipt_or_dispatch(string operation)
    {
        var h = new Harness { PolicyAllowed = false };
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(StationReservationState.Denied,
            await InvokeAsync(h, operation, PropertyId, cancellationToken: cancellation.Token));

        Assert.Equal(["provider", "country"], h.Calls);
        AssertCountryCall(Assert.Single(h.Country.Calls), cancellation.Token);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(25, true)]
    [InlineData(100, true)]
    public async Task List_forwards_exact_server_queue_coordinates_and_returns_owner_page(int pageSize, bool withCursor)
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        StationArrivalCursor? after = withCursor ? new(Today.AddDays(-1), "MORGAN GUEST", Id(30)) : null;
        var continuation = new StationArrivalCursor(Today, "ZOE GUEST", Id(31));
        var page = new StationDueArrivalPage(StationReservationState.Ready, [Arrival()], continuation);
        h.Page = page;

        var result = await h.Service.ListAsync(PropertyId, Today, pageSize, after, cancellation.Token);

        Assert.Same(page, result);
        Assert.Equal(["provider", "country", "list"], h.Calls);
        AssertCountryCall(Assert.Single(h.Country.Calls), cancellation.Token);
        var call = Assert.Single(h.Arrivals.ListCalls);
        Assert.Equal(PropertyId, call.PropertyId);
        Assert.Equal(Today, call.LocalDate);
        Assert.Equal(pageSize, call.PageSize);
        Assert.Same(after, call.After);
        Assert.Equal(cancellation.Token, call.Token);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(StationReservationState.Incomplete)]
    [InlineData(StationReservationState.Unavailable)]
    [InlineData(StationReservationState.Unsupported)]
    public async Task List_preserves_typed_owner_queue_failure_without_fabricating_ready(StationReservationState state)
    {
        var h = new Harness { Page = new(state, []) };

        var result = await h.Service.ListAsync(PropertyId, Today, 25, null);

        Assert.Same(h.Page, result);
        Assert.Equal(state, result.State);
        Assert.Empty(result.Items);
        Assert.Null(result.Continuation);
        Assert.Equal(["provider", "country", "list"], h.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("reservation")]
    [InlineData("zero-version")]
    [InlineData("negative-version")]
    [InlineData("date")]
    public async Task Invalid_prepare_coordinates_conflict_before_any_collaborator(string invalid)
    {
        var h = new Harness();
        var date = invalid == "date" ? default : Today;

        var result = await h.Service.PrepareAsync(PropertyId,
            invalid == "reservation" ? Guid.Empty : ReservationId,
            invalid == "operation" ? Guid.Empty : OperationId,
            invalid == "zero-version" ? 0 : invalid == "negative-version" ? -1 : 9,
            date, Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Equal(date, result.BusinessDate);
        Assert.Null(result.Arrival);
        Assert.False(result.Replay);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("prepare", "station")]
    [InlineData("prepare", "browser")]
    [InlineData("prepare", "staff")]
    [InlineData("prepare", "actor")]
    [InlineData("prepare", "zero-generation")]
    [InlineData("prepare", "negative-generation")]
    [InlineData("prepare", "unknown-authority")]
    [InlineData("prepare", "foreign-authority")]
    [InlineData("check-in", "station")]
    [InlineData("check-in", "browser")]
    [InlineData("check-in", "staff")]
    [InlineData("check-in", "actor")]
    [InlineData("check-in", "zero-generation")]
    [InlineData("check-in", "negative-generation")]
    [InlineData("check-in", "unknown-authority")]
    [InlineData("check-in", "foreign-authority")]
    public async Task Invalid_provenance_conflicts_before_protected_calls(string operation, string invalid)
    {
        var h = new Harness();

        var state = await InvokeAsync(h, operation, PropertyId, provenance: InvalidProvenance(invalid));

        Assert.Equal(StationReservationState.Conflict, state);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Fact]
    public async Task Invisible_reservation_is_denied_before_receipt_or_attribution_access()
    {
        var h = new Harness { Visible = false, Prior = Prior() };
        using var cancellation = new CancellationTokenSource();

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance, cancellation.Token);

        Assert.Equal(StationReservationState.Denied, result.State);
        Assert.Null(result.Arrival);
        Assert.False(result.Replay);
        Assert.Equal(["provider", "country", "visible"], h.Calls);
        Assert.Equal((PropertyId, ReservationId, cancellation.Token), Assert.Single(h.Arrivals.VisibilityCalls));
        Assert.Empty(h.Operations.ReadCalls);
        Assert.Empty(h.Operations.AttributionCalls);
    }

    [Fact]
    public async Task Non_replay_prepares_specific_visible_reservation_not_membership_in_a_queue_page()
    {
        var h = new Harness { Page = new(StationReservationState.Ready, []) };
        using var cancellation = new CancellationTokenSource();

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance, cancellation.Token);

        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Equal(Today, result.BusinessDate);
        Assert.False(result.Replay);
        Assert.Same(h.Target, result.Arrival);
        Assert.Equal(Id(20), result.Arrival!.AllocationId);
        Assert.Equal(4, result.Arrival.AllocationVersion);
        Assert.Equal([Id(21), Id(22)], result.Arrival.Units.Select(unit => unit.InventoryUnitId));
        Assert.Equal(["provider", "country", "visible", "operation", "find"], h.Calls);
        AssertCountryCall(Assert.Single(h.Country.Calls), cancellation.Token);
        Assert.Equal((ReservationId, OperationId, cancellation.Token), Assert.Single(h.Operations.ReadCalls));
        Assert.Equal((PropertyId, ReservationId, Today, cancellation.Token), Assert.Single(h.Arrivals.FindCalls));
        Assert.Empty(h.Arrivals.ListCalls);
        Assert.Empty(h.Operations.AttributionCalls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_revised_arrival_is_incomplete_without_partial_preparation(bool missing)
    {
        var h = new Harness { Target = missing ? null : Arrival() with { ExpectedVersion = 10 } };

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance);

        Assert.Equal(StationReservationState.Incomplete, result.State);
        Assert.Equal(Today, result.BusinessDate);
        Assert.Null(result.Arrival);
        Assert.False(result.Replay);
        Assert.Equal(["provider", "country", "visible", "operation", "find"], h.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("missing-version")]
    [InlineData("details-revision")]
    [InlineData("missing-date")]
    [InlineData("fingerprint")]
    [InlineData("empty-fingerprint")]
    public async Task Incompatible_parent_receipt_conflicts_without_due_arrival_lookup(string mismatch)
    {
        var prior = Prior();
        var h = new Harness
        {
            Prior = mismatch switch
            {
                "property" => prior with { PropertyId = Id(100) },
                "kind" => prior with { Kind = ReservationManagementOperationKind.CheckOut },
                "version" => prior with { ExpectedVersion = 10 },
                "missing-version" => prior with { ExpectedVersion = null },
                "details-revision" => prior with { ExpectedDetailsRevision = 1 },
                "missing-date" => prior with { BusinessDate = null },
                "fingerprint" => prior with { RequestFingerprint = "ordinary-receipt" },
                "empty-fingerprint" => prior with { RequestFingerprint = string.Empty },
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
            }
        };

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Equal(Today, result.BusinessDate);
        Assert.Null(result.Arrival);
        Assert.False(result.Replay);
        Assert.Equal(["provider", "country", "visible", "operation", "attribution"], h.Calls);
        Assert.Empty(h.Arrivals.FindCalls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("station")]
    [InlineData("browser")]
    [InlineData("staff")]
    [InlineData("actor")]
    [InlineData("generation")]
    [InlineData("authority")]
    [InlineData("unknown-authority")]
    [InlineData("foreign-authority")]
    [InlineData("missing-child")]
    [InlineData("unsupported-child")]
    public async Task Replay_requires_supported_exact_immutable_attribution_without_primary_or_foreign_fallback(string mismatch)
    {
        var changed = mismatch switch
        {
            "station" => Provenance with { StationId = Id(100) },
            "browser" => Provenance with { BrowserSessionId = Id(100) },
            "staff" => Provenance with { StaffMemberId = Id(100) },
            "actor" => Provenance with { ActorSessionId = Id(100) },
            "generation" => Provenance with { Generation = 8 },
            "authority" => Provenance with { Authority = StationReservationAuthority.StationOnly },
            "unknown-authority" => Provenance with { Authority = StationReservationAuthority.Unknown },
            "foreign-authority" => Provenance with { Authority = (StationReservationAuthority)99 },
            "missing-child" or "unsupported-child" => Provenance,
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var h = new Harness
        {
            Prior = Prior(),
            Attribution = new(mismatch != "unsupported-child", mismatch == "missing-child" ? null : changed)
        };

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Null(result.Arrival);
        Assert.False(result.Replay);
        Assert.Equal(["provider", "country", "visible", "operation", "attribution"], h.Calls);
        Assert.Empty(h.Arrivals.FindCalls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(StationReservationAuthority.LinkedStation)]
    [InlineData(StationReservationAuthority.StationOnly)]
    public async Task Exact_replay_uses_original_server_business_date_without_fetching_current_due_arrival(
        StationReservationAuthority authority)
    {
        var provenance = Provenance with { Authority = authority, JobGrantRevision = authority == StationReservationAuthority.StationOnly ? 3 : null };
        var h = new Harness { Prior = Prior(), Attribution = new(true, provenance), Target = null };
        using var cancellation = new CancellationTokenSource();

        var result = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, provenance, cancellation.Token);

        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Equal(Today.AddDays(-1), result.BusinessDate);
        Assert.NotEqual(Today, result.BusinessDate);
        Assert.True(result.Replay);
        Assert.Null(result.Arrival);
        Assert.Equal(["provider", "country", "visible", "operation", "attribution"], h.Calls);
        Assert.Equal((ReservationId, OperationId, cancellation.Token), Assert.Single(h.Operations.ReadCalls));
        Assert.Equal((ReservationId, OperationId, cancellation.Token), Assert.Single(h.Operations.AttributionCalls));
        Assert.Empty(h.Arrivals.ListCalls);
        Assert.Empty(h.Arrivals.FindCalls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("reservation")]
    [InlineData("zero-version")]
    [InlineData("negative-version")]
    [InlineData("date")]
    [InlineData("missing-arrival")]
    [InlineData("arrival-reservation")]
    [InlineData("arrival-version")]
    public async Task Invalid_check_in_coordinates_conflict_before_policy_and_dispatch(string invalid)
    {
        var h = new Harness();
        var preparation = Preparation();
        preparation = invalid switch
        {
            "date" => preparation with { BusinessDate = default },
            "missing-arrival" => preparation with { Arrival = null },
            "arrival-reservation" => preparation with { Arrival = Arrival() with { ReservationId = Id(100) } },
            "arrival-version" => preparation with { Arrival = Arrival() with { ExpectedVersion = 10 } },
            _ => preparation
        };

        var result = await h.Service.CheckInAsync(PropertyId,
            invalid == "reservation" ? Guid.Empty : ReservationId,
            invalid == "operation" ? Guid.Empty : OperationId,
            invalid == "zero-version" ? 0 : invalid == "negative-version" ? -1 : 9,
            preparation, Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Null(result.Receipt);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(StationReservationState.Denied)]
    [InlineData(StationReservationState.Unavailable)]
    [InlineData(StationReservationState.Unsupported)]
    [InlineData(StationReservationState.Incomplete)]
    [InlineData(StationReservationState.Conflict)]
    [InlineData(StationReservationState.Applied)]
    public async Task Non_ready_preparation_never_dispatches(StationReservationState state)
    {
        var h = new Harness();

        var result = await h.Service.CheckInAsync(PropertyId, ReservationId, OperationId, 9,
            Preparation() with { State = state }, Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Null(result.Receipt);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_in_rechecks_current_policy_and_provider_after_successful_preparation(bool providerLost)
    {
        var h = new Harness();
        var prepared = await h.Service.PrepareAsync(PropertyId, ReservationId, OperationId, 9, Today, Provenance);
        Assert.Equal(StationReservationState.Ready, prepared.State);
        h.Supported = !providerLost;
        h.PolicyAllowed = false;

        var result = await h.Service.CheckInAsync(PropertyId, ReservationId, OperationId, 9, prepared, Provenance);

        Assert.Equal(providerLost ? StationReservationState.Unsupported : StationReservationState.Denied, result.State);
        Assert.Null(result.Receipt);
        string[] expectedCalls = providerLost
            ? ["provider", "country", "visible", "operation", "find", "provider"]
            : ["provider", "country", "visible", "operation", "find", "provider", "country"];
        Assert.Equal(expectedCalls, h.Calls);
        Assert.Equal(providerLost ? 1 : 2, h.Country.Calls.Count);
        Assert.Empty(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData(false, StationReservationAuthority.LinkedStation)]
    [InlineData(false, StationReservationAuthority.StationOnly)]
    [InlineData(true, StationReservationAuthority.LinkedStation)]
    [InlineData(true, StationReservationAuthority.StationOnly)]
    public async Task Check_in_forwards_one_actual_command_and_maps_dispatcher_receipt_without_claiming_persistence(
        bool replay, StationReservationAuthority authority)
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var provenance = Provenance with { Authority = authority, JobGrantRevision = authority == StationReservationAuthority.StationOnly ? 3 : null };
        var date = replay ? Today.AddDays(-1) : Today;
        var preparation = new StationCheckInPreparation(StationReservationState.Ready, date, replay ? null : Arrival(), replay);

        var result = await h.Service.CheckInAsync(PropertyId, ReservationId, OperationId, 9, preparation, provenance, cancellation.Token);

        Assert.Equal(StationReservationState.Applied, result.State);
        Assert.Same(h.Dispatcher.Receipt, result.Receipt);
        Assert.Equal(["provider", "country", "dispatch"], h.Calls);
        AssertCountryCall(Assert.Single(h.Country.Calls), cancellation.Token);
        var dispatched = Assert.Single(h.Dispatcher.Commands);
        var command = Assert.IsType<StationCheckInReservationCommand>(dispatched.Command);
        Assert.Equal(OperationId, command.OperationId);
        Assert.Equal(PropertyId, command.PropertyId);
        Assert.Equal(ReservationId, command.ReservationId);
        Assert.Equal(date, command.BusinessDate);
        Assert.Equal(9, command.ExpectedVersion);
        Guid? expectedAllocationId = replay ? null : Id(20);
        long? expectedAllocationVersion = replay ? null : 4L;
        Assert.Equal(expectedAllocationId, command.AllocationId);
        Assert.Equal(expectedAllocationVersion, command.AllocationVersion);
        Assert.Equal(provenance, command.Provenance);
        Assert.Equal(cancellation.Token, dispatched.Token);
        Assert.Empty(h.Operations.ReadCalls);
        Assert.Empty(h.Arrivals.FindCalls);
    }

    [Fact]
    public async Task Dispatcher_domain_failure_maps_conflict_without_success_receipt()
    {
        var h = new Harness();
        h.Dispatcher.Succeeds = false;

        var result = await h.Service.CheckInAsync(PropertyId, ReservationId, OperationId, 9, Preparation(), Provenance);

        Assert.Equal(StationReservationState.Conflict, result.State);
        Assert.Null(result.Receipt);
        Assert.Equal(["provider", "country", "dispatch"], h.Calls);
        Assert.Single(h.Dispatcher.Commands);
    }

    [Theory]
    [InlineData("list", "country", "exception")]
    [InlineData("list", "list", "exception")]
    [InlineData("prepare", "country", "exception")]
    [InlineData("prepare", "visible", "exception")]
    [InlineData("prepare", "operation", "exception")]
    [InlineData("prepare", "attribution", "exception")]
    [InlineData("prepare", "find", "exception")]
    [InlineData("check-in", "country", "exception")]
    [InlineData("check-in", "dispatch", "exception")]
    [InlineData("list", "country", "caller-cancelled")]
    [InlineData("list", "list", "caller-cancelled")]
    [InlineData("prepare", "country", "caller-cancelled")]
    [InlineData("prepare", "visible", "caller-cancelled")]
    [InlineData("prepare", "operation", "caller-cancelled")]
    [InlineData("prepare", "attribution", "caller-cancelled")]
    [InlineData("prepare", "find", "caller-cancelled")]
    [InlineData("check-in", "country", "caller-cancelled")]
    [InlineData("check-in", "dispatch", "caller-cancelled")]
    [InlineData("list", "country", "uncancelled-oce")]
    [InlineData("list", "list", "uncancelled-oce")]
    [InlineData("prepare", "country", "uncancelled-oce")]
    [InlineData("prepare", "visible", "uncancelled-oce")]
    [InlineData("prepare", "operation", "uncancelled-oce")]
    [InlineData("prepare", "attribution", "uncancelled-oce")]
    [InlineData("prepare", "find", "uncancelled-oce")]
    [InlineData("check-in", "country", "uncancelled-oce")]
    [InlineData("check-in", "dispatch", "uncancelled-oce")]
    public async Task Failure_stops_at_its_owner_boundary_and_only_caller_cancellation_propagates(
        string operation, string step, string failure)
    {
        using var cancellation = new CancellationTokenSource();
        if (failure == "caller-cancelled")
        {
            cancellation.Cancel();
        }

        var h = new Harness
        {
            Prior = step == "attribution" ? Prior() : null,
            ThrowAt = step,
            Failure = failure == "exception" ? new InvalidOperationException("Synthetic owner failure") : new OperationCanceledException(cancellation.Token)
        };

        if (failure == "caller-cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(h, operation, PropertyId, cancellationToken: cancellation.Token));
        }
        else
        {
            Assert.Equal(StationReservationState.Unavailable, await InvokeAsync(h, operation, PropertyId, cancellationToken: cancellation.Token));
        }

        string[] path = operation switch
        {
            "list" => ["provider", "country", "list"],
            "prepare" => ["provider", "country", "visible", "operation", step == "attribution" ? "attribution" : "find"],
            "check-in" => ["provider", "country", "dispatch"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Assert.Equal(path.Take(Array.IndexOf(path, step) + 1), h.Calls);
        Assert.All(h.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    private static async Task<StationReservationState> InvokeAsync(Harness h, string operation, Guid propertyId,
        StationCheckInProvenance? provenance = null, CancellationToken cancellationToken = default) => operation switch
        {
            "list" => (await h.Service.ListAsync(propertyId, Today, 25, null, cancellationToken)).State,
            "prepare" => (await h.Service.PrepareAsync(propertyId, ReservationId, OperationId, 9, Today, provenance ?? Provenance, cancellationToken)).State,
            "check-in" => (await h.Service.CheckInAsync(propertyId, ReservationId, OperationId, 9, Preparation(), provenance ?? Provenance, cancellationToken)).State,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static StationCheckInProvenance InvalidProvenance(string invalid) => invalid switch
    {
        "station" => Provenance with { StationId = Guid.Empty },
        "browser" => Provenance with { BrowserSessionId = Guid.Empty },
        "staff" => Provenance with { StaffMemberId = Guid.Empty },
        "actor" => Provenance with { ActorSessionId = Guid.Empty },
        "zero-generation" => Provenance with { Generation = 0 },
        "negative-generation" => Provenance with { Generation = -1 },
        "unknown-authority" => Provenance with { Authority = StationReservationAuthority.Unknown },
        "foreign-authority" => Provenance with { Authority = (StationReservationAuthority)99 },
        _ => throw new ArgumentOutOfRangeException(nameof(invalid))
    };

    private static StationDueArrival Arrival() => new(ReservationId, "Morgan Guest", Today, Today.AddDays(3), 9, Id(20), 4,
        [new(Id(21), Id(23), Id(24), 2, 5, 6), new(Id(22), Id(25), null, 1, 7, 8)]);

    private static StationCheckInPreparation Preparation() => new(StationReservationState.Ready, Today, Arrival());

    private static ReservationManagementOperationRecord Prior() => new(OperationId, "station-reservation-owner-a", PropertyId,
        ReservationId, ReservationManagementOperationKind.CheckIn, 9, null, Today.AddDays(-1), Now.AddDays(-1));

    private static void AssertCountryCall(CountryCall call, CancellationToken token)
    {
        Assert.Equal(PropertyId, call.PropertyId);
        Assert.Equal(ReservationCountryPolicyAdmission.ReservationManagementPurpose, call.Purpose);
        Assert.Equal(CountryPolicySurface.ApiWrite, call.Surface);
        Assert.Equal(ReservationCountryPolicyAdmission.AuthorizedOperatorProvenance, call.Provenance);
        Assert.Equal(token, call.Token);
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed record CountryCall(Guid PropertyId, string Purpose, CountryPolicySurface Surface, string Provenance, CancellationToken Token);
    private sealed record ListCall(Guid PropertyId, DateOnly LocalDate, int PageSize, StationArrivalCursor? After, CancellationToken Token);

    private sealed class Harness
    {
        public Harness()
        {
            this.Arrivals = new(this);
            this.Operations = new(this);
            this.Country = new(this);
            this.Dispatcher = new(this);
            this.Service = new(this.Arrivals, this.Operations, this.Country, this.Dispatcher);
        }

        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public bool Supported { get; set; } = true;
        public bool PolicyAllowed { get; set; } = true;
        public bool Visible { get; set; } = true;
        public StationDueArrival? Target { get; set; } = Arrival();
        public StationDueArrivalPage Page { get; set; } = new(StationReservationState.Ready, []);
        public ReservationManagementOperationRecord? Prior { get; set; }
        public ReservationStationAttributionRead Attribution { get; set; } = new(true, Provenance);
        public string? ThrowAt { get; set; }
        public Exception? Failure { get; set; }
        public ArrivalRepository Arrivals { get; }
        public OperationRepository Operations { get; }
        public CountryAdmission Country { get; }
        public Dispatcher Dispatcher { get; }
        public StationReservationOperations Service { get; }

        public void Record(string step, CancellationToken? token = null)
        {
            this.Calls.Add(step);
            if (token is { } value)
            {
                this.Tokens.Add(value);
            }

            if (this.ThrowAt == step)
            {
                throw this.Failure ?? new InvalidOperationException("Synthetic owner failure");
            }
        }
    }

    private sealed class ArrivalRepository(Harness h) : IStationDueArrivalRepository
    {
        public List<ListCall> ListCalls { get; } = [];
        public List<(Guid PropertyId, Guid ReservationId, DateOnly LocalDate, CancellationToken Token)> FindCalls { get; } = [];
        public List<(Guid PropertyId, Guid ReservationId, CancellationToken Token)> VisibilityCalls { get; } = [];

        public bool SupportsStationOperations
        {
            get
            {
                h.Record("provider");
                return h.Supported;
            }
        }

        public Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
            StationArrivalCursor? after, CancellationToken cancellationToken)
        {
            this.ListCalls.Add(new(propertyId, localDate, pageSize, after, cancellationToken));
            h.Record("list", cancellationToken);
            return Task.FromResult(h.Page);
        }

        public Task<StationDueArrival?> FindAsync(Guid propertyId, Guid reservationId, DateOnly localDate, CancellationToken cancellationToken)
        {
            this.FindCalls.Add((propertyId, reservationId, localDate, cancellationToken));
            h.Record("find", cancellationToken);
            return Task.FromResult(h.Target);
        }

        public Task<bool> IsVisibleAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken)
        {
            this.VisibilityCalls.Add((propertyId, reservationId, cancellationToken));
            h.Record("visible", cancellationToken);
            return Task.FromResult(h.Visible);
        }
    }

    private sealed class OperationRepository(Harness h) : IReservationManagementOperationRepository
    {
        public List<(Guid ReservationId, Guid OperationId, CancellationToken Token)> ReadCalls { get; } = [];
        public List<(Guid ReservationId, Guid OperationId, CancellationToken Token)> AttributionCalls { get; } = [];

        public Task<ReservationManagementOperationRecord?> GetAsync(Guid reservationId, Guid operationId, CancellationToken cancellationToken)
        {
            this.ReadCalls.Add((reservationId, operationId, cancellationToken));
            h.Record("operation", cancellationToken);
            return Task.FromResult(h.Prior);
        }

        public Task<ReservationStationAttributionRead> GetStationAttributionAsync(Guid reservationId, Guid operationId, CancellationToken cancellationToken)
        {
            this.AttributionCalls.Add((reservationId, operationId, cancellationToken));
            h.Record("attribution", cancellationToken);
            return Task.FromResult(h.Attribution);
        }

        public Task AddAsync(ReservationManagementOperationRecord operation, CancellationToken cancellationToken)
        {
            h.Record("unexpected-parent-write", cancellationToken);
            throw new InvalidOperationException("This wrapper must not persist a receipt directly.");
        }

        public Task AddStationAsync(ReservationManagementOperationRecord operation, StationCheckInProvenance provenance,
            long resultingVersion, CancellationToken cancellationToken)
        {
            h.Record("unexpected-attribution-write", cancellationToken);
            throw new InvalidOperationException("The real transactional dispatcher owns attribution persistence.");
        }
    }

    private sealed class CountryAdmission(Harness h) : IReservationCountryPolicyAdmission
    {
        public List<CountryCall> Calls { get; } = [];

        public Task<CountryPolicyDecision> EvaluateAsync(Guid propertyId, string purposeCode, CountryPolicySurface surface,
            string sourceProvenance, CancellationToken cancellationToken)
        {
            this.Calls.Add(new(propertyId, purposeCode, surface, sourceProvenance, cancellationToken));
            h.Record("country", cancellationToken);
            return Task.FromResult(h.PolicyAllowed
                ? CountryPolicyDecision.Allow(new CountryPolicyEvidence("GB", "gb-hostel", 1, "eu-west-2", "uk-no-transfer",
                    "reservation-operational", 1, new string('a', 64), purposeCode, surface, sourceProvenance,
                    CountryPolicyApprovalState.Approved, Now.AddDays(-1), Now.AddDays(30), Now, []))
                : CountryPolicyDecision.Deny(CountryPolicyDecisionReason.CountryDisabled));
        }
    }

    private sealed class Dispatcher(Harness h) : IRequestDispatcher
    {
        public List<(object Command, CancellationToken Token)> Commands { get; } = [];
        public bool Succeeds { get; set; } = true;
        public ReservationMutationReceiptDto Receipt { get; } = new(ReservationId, PropertyId, ReservationStatus.CheckedIn, 3, 10);

        public Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            this.Commands.Add((command, cancellationToken));
            h.Record("dispatch", cancellationToken);
            Assert.IsType<StationCheckInReservationCommand>(command);
            object result = this.Succeeds ? Result.Success(this.Receipt)
                : Result.Failure<ReservationMutationReceiptDto>(new Error("Station.TestConflict", "Synthetic domain conflict"));
            return Task.FromResult((Result<TResponse>)result);
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            h.Record("unexpected-dispatch-query", cancellationToken);
            throw new InvalidOperationException("The owner sends a check-in command, not a dispatcher query.");
        }
    }
}
