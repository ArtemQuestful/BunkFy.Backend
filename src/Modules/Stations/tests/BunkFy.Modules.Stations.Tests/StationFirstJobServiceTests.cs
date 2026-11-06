namespace BunkFy.Modules.Stations.Tests;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Contracts.Stations;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Real first-job orchestration and admission with explicit owner-boundary fakes; no persistence or HTTP proof.</summary>
public sealed partial class StationFirstJobServiceTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private const string Subject = "bb000000-0000-0000-0000-000000000002";
    private const string Credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Property = Id(1);
    private static readonly Guid Staff = Id(2);
    private static readonly Guid Operation = Id(3);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly LocalDate = new(2026, 9, 28);
    private static readonly string[] HandoffOnlyEvents = ["bootstrap", "runtime"];

    [Theory]
    [InlineData("active", StationReservationState.Denied)]
    [InlineData("missing", StationReservationState.Denied)]
    [InlineData("unavailable", StationReservationState.Unavailable)]
    public async Task Handoff_failure_prevents_all_guest_job_reads_and_mutations(string condition, StationReservationState expected)
    {
        using var fixture = new Fixture();
        fixture.Handoff.Active = condition == "active";
        fixture.Handoff.SessionUnavailable = condition == "unavailable";
        if (condition == "missing")
        {
            fixture.Handoff.Receipt = null;
        }
        var page = await fixture.ListAsync();
        Assert.Equal(expected, page.State);
        Assert.Empty(page.Items);
        Assert.Null(page.PropertyId);
        Assert.Equal(expected, (await fixture.CheckInAsync()).State);
        Assert.All(fixture.Events, name => Assert.Contains(name, HandoffOnlyEvents));
    }
    private static readonly string[] ReservationFieldNames = ["AllocationId", "AllocationVersion", "Arrival", "Departure",
        "ExpectedVersion", "PrimaryGuestName", "ReservationId", "Units"];
    private static readonly string[] ArrivalFieldNames = ["Places", "Reservation"];

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public async Task Invalid_credentials_never_bootstrap_or_read_protected_owners(string credential)
    {
        using var f = new Fixture();
        AssertPageFailure(await f.Service.ListAsync(credential, f.Actor), StationReservationState.Denied);
        Assert.Equal(StationReservationState.Denied, (await f.CheckInAsync(credential: credential)).State);
        Assert.Equal(0, f.BootstrapReads);
        Assert.Empty(f.Events);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("session")]
    [InlineData("generation-zero")]
    [InlineData("generation-negative")]
    [InlineData("authority-unknown")]
    [InlineData("authority-undefined")]
    public async Task Invalid_actor_coordinates_fail_before_bootstrap(string scenario)
    {
        using var f = new Fixture();
        var actor = scenario switch
        {
            "staff" => f.Actor with { StaffMemberId = Guid.Empty },
            "session" => f.Actor with { ActorSessionId = Guid.Empty },
            "generation-zero" => f.Actor with { Generation = 0 },
            "generation-negative" => f.Actor with { Generation = -1 },
            "authority-unknown" => f.Actor with { AuthorityKind = StationAuthorityKind.Unknown },
            "authority-undefined" => f.Actor with { AuthorityKind = (StationAuthorityKind)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        AssertPageFailure(await f.Service.ListAsync(Credential, actor), StationReservationState.Denied);
        Assert.Equal(StationReservationState.Denied, (await f.CheckInAsync(actor: actor)).State);
        Assert.Equal(0, f.BootstrapReads);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tenant-invalid")]
    [InlineData("tenant-empty")]
    [InlineData("tenant-noncanonical")]
    [InlineData("station")]
    [InlineData("browser")]
    [InlineData("property")]
    public async Task Missing_or_malformed_server_device_never_enters_a_protected_scope(string scenario)
    {
        using var f = new Fixture();
        f.Device = scenario switch
        {
            "unknown" => null,
            "tenant-invalid" => f.Device! with { ScopeId = "not-a-tenant" },
            "tenant-empty" => f.Device! with { ScopeId = Guid.Empty.ToString("D") },
            "tenant-noncanonical" => f.Device! with { ScopeId = Tenant.ToUpperInvariant() },
            "station" => f.Device! with { StationId = Guid.Empty },
            "browser" => f.Device! with { BrowserSessionId = Guid.Empty },
            "property" => f.Device! with { PropertyId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Denied);
        Assert.Equal(StationReservationState.Denied, (await f.CheckInAsync()).State);
        Assert.DoesNotContain("runtime", f.Events);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("expired-or-revoked-device")]
    [InlineData("expired-actor")]
    [InlineData("unregistered")]
    [InlineData("locked")]
    [InlineData("changed-staff")]
    [InlineData("changed-session")]
    [InlineData("changed-generation")]
    [InlineData("changed-authority")]
    [InlineData("session-generation")]
    [InlineData("session-property")]
    [InlineData("session-station")]
    [InlineData("session-browser")]
    [InlineData("credential-missing")]
    [InlineData("credential-revoked")]
    [InlineData("credential-staff")]
    [InlineData("enrollment-unbound")]
    public async Task Noncurrent_runtime_facts_prevent_all_reservation_and_inventory_access(string scenario)
    {
        using var f = new Fixture();
        var facts = f.Facts!;
        f.Facts = scenario switch
        {
            // The runtime-store contract owns expiry/revocation and returns no device or ActorCurrent=false.
            "expired-or-revoked-device" => null,
            "expired-actor" => facts with { ActorCurrent = false },
            "unregistered" => facts with { Registered = false },
            "locked" => facts with { Session = facts.Session with { Actor = null } },
            "changed-staff" => facts with { Session = facts.Session with { Actor = f.Actor with { StaffMemberId = Id(900) } } },
            "changed-session" => facts with { Session = facts.Session with { Actor = f.Actor with { ActorSessionId = Id(900) } } },
            "changed-generation" => facts with { Session = facts.Session with { Actor = f.Actor with { Generation = 8 } } },
            "changed-authority" => facts with { Session = facts.Session with { Actor = f.Actor with { AuthorityKind = StationAuthorityKind.StationOnly } } },
            "session-generation" => facts with { Session = facts.Session with { Generation = 8 } },
            "session-property" => facts with { Session = facts.Session with { PropertyId = Id(900) } },
            "session-station" => facts with { Session = facts.Session with { StationId = Id(900) } },
            "session-browser" => facts with { Session = facts.Session with { BrowserSessionId = Id(900) } },
            "credential-missing" => facts with { Credential = null },
            "credential-revoked" => facts with { Credential = facts.Credential! with { Revoked = true } },
            "credential-staff" => facts with { Credential = facts.Credential! with { StaffMemberId = Id(900) } },
            "enrollment-unbound" => facts with { Credential = facts.Credential! with { Enrollment = new(StationActorKind.Unknown, null) } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Denied);
        Assert.Equal(StationReservationState.Denied, (await f.CheckInAsync()).State);
        Assert.Equal(0, f.LinkedReads);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(3L, true)]
    public async Task Station_only_actor_needs_a_current_scoped_grant(long? revision, bool revoked)
    {
        using var f = new Fixture(StationAuthorityKind.StationOnly);
        f.CheckInGrant = revision is { } value ? new(value, revoked) : null;
        AssertPageFailure(await f.ListAsync(), StationReservationState.Denied);
        Assert.Equal(StationReservationState.Denied, (await f.CheckInAsync()).State);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData(WorkspaceStaffStationObservationStatus.PrerequisitesNotObserved, StationReservationState.Denied)]
    [InlineData(WorkspaceStaffStationObservationStatus.ObservationStale, StationReservationState.Conflict)]
    [InlineData(WorkspaceStaffStationObservationStatus.Unavailable, StationReservationState.Unavailable)]
    [InlineData(WorkspaceStaffStationObservationStatus.Unknown, StationReservationState.Unavailable)]
    public async Task Linked_authority_is_current_before_any_protected_lookup(
        WorkspaceStaffStationObservationStatus authority, StationReservationState expected)
    {
        using var f = new Fixture { LinkedStatus = authority };
        AssertPageFailure(await f.ListAsync(), expected);
        Assert.Equal(expected, (await f.CheckInAsync()).State);
        Assert.Equal(2, f.LinkedReads);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("staff-suspended", StationReservationState.Denied)]
    [InlineData("staff-link-changed", StationReservationState.Conflict)]
    [InlineData("property-retired", StationReservationState.Denied)]
    [InlineData("property-context", StationReservationState.Unavailable)]
    [InlineData("timezone-unknown", StationReservationState.Unavailable)]
    [InlineData("workspace-restricted", StationReservationState.Denied)]
    [InlineData("workspace-unavailable", StationReservationState.Unavailable)]
    [InlineData("organization-closed", StationReservationState.Denied)]
    public async Task Owner_admission_failures_reveal_no_queue_context(string scenario, StationReservationState expected)
    {
        using var f = new Fixture();
        switch (scenario)
        {
            case "staff-suspended":
                f.StaffStatus = StaffStatus.Suspended;
                break;
            case "staff-link-changed":
                f.AuthSubject = Id(901).ToString("D");
                break;
            case "property-retired":
                f.PropertyFacts = f.PropertyFacts with { Status = PropertyStatus.Retired };
                break;
            case "property-context":
                f.PropertyFacts = f.PropertyFacts with { ScopeId = Id(901).ToString("D") };
                break;
            case "timezone-unknown":
                f.PropertyFacts = f.PropertyFacts with { TimeZoneId = "Not/A_Zone" };
                break;
            case "workspace-restricted":
                f.Workspace = WorkspaceOperationalAdmissionDecision.Restricted;
                break;
            case "workspace-unavailable":
                f.Workspace = WorkspaceOperationalAdmissionDecision.Unavailable;
                break;
            case "organization-closed":
                f.OrganizationStatus = OrganizationScopeStatus.Closed;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        AssertPageFailure(await f.ListAsync(), expected);
        Assert.Equal(expected, (await f.CheckInAsync()).State);
        AssertNoProtectedCalls(f);
    }

    [Fact]
    public async Task Authority_is_reobserved_after_initial_admission_before_first_reservation_read()
    {
        using var f = new Fixture();
        f.OnEvent = name =>
        {
            if (name == "linked")
            {
                var facts = f.Facts!;
                f.Facts = facts with { Credential = facts.Credential! with { Revision = 9 } };
            }
        };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Conflict);
        AssertNoProtectedCalls(f);
    }

    [Theory]
    [InlineData("list", "actor", StationReservationState.Conflict)]
    [InlineData("list", "permission", StationReservationState.Denied)]
    [InlineData("list", "date", StationReservationState.Conflict)]
    [InlineData("prepare", "actor", StationReservationState.Conflict)]
    [InlineData("prepare", "permission", StationReservationState.Denied)]
    [InlineData("prepare", "date", StationReservationState.Conflict)]
    public async Task Reservation_owner_reads_are_bracketed_before_label_lookup(
        string boundary, string change, StationReservationState expected)
    {
        using var f = new Fixture();
        f.OnEvent = name => { if (name == boundary) { f.ChangeAuthority(change); } };
        if (boundary == "list")
        {
            AssertPageFailure(await f.ListAsync(), expected);
        }
        else
        {
            Assert.Equal(expected, (await f.CheckInAsync()).State);
        }
        Assert.Empty(f.Inventory.Batches);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Theory]
    [InlineData(false, "actor", StationReservationState.Conflict)]
    [InlineData(false, "permission", StationReservationState.Denied)]
    [InlineData(false, "date", StationReservationState.Conflict)]
    [InlineData(true, "actor", StationReservationState.Conflict)]
    [InlineData(true, "permission", StationReservationState.Denied)]
    [InlineData(true, "date", StationReservationState.Conflict)]
    public async Task Last_inventory_read_is_rechecked_before_protected_return_or_dispatch(
        bool checkIn, string change, StationReservationState expected)
    {
        using var f = new Fixture();
        f.OnEvent = name => { if (name == "labels") { f.ChangeAuthority(change); } };
        if (checkIn)
        {
            Assert.Equal(expected, (await f.CheckInAsync()).State);
        }
        else
        {
            AssertPageFailure(await f.ListAsync(), expected);
        }
        Assert.Single(f.Inventory.Batches);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task Changed_actor_between_inventory_batches_stops_remaining_reads_and_discards_the_page()
    {
        using var f = new Fixture();
        f.SetArrivals(Arrival(100, Enumerable.Range(1, 26).Select(Unit).ToArray()));
        f.OnEvent = name => { if (name == "labels") { f.ChangeAuthority("actor"); } };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Conflict);
        Assert.Equal(25, Assert.Single(f.Inventory.Batches).Length);
    }

    [Fact]
    public async Task Station_only_grant_loss_after_reservation_read_prevents_inventory_disclosure()
    {
        using var f = new Fixture(StationAuthorityKind.StationOnly);
        f.OnEvent = name =>
        {
            if (name == "list")
            {
                f.CheckInGrant = f.CheckInGrant! with { Revoked = true };
            }
        };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Denied);
        Assert.Empty(f.Inventory.Batches);
        Assert.Equal(0, f.LinkedReads);
    }

    [Fact]
    public async Task Check_in_result_is_not_disclosed_if_current_authority_is_lost_after_dispatch()
    {
        using var f = new Fixture();
        f.OnEvent = name => { if (name == "dispatch") { f.ChangeAuthority("permission"); } };
        var result = await f.CheckInAsync();
        Assert.Equal(StationReservationState.Denied, result.State);
        Assert.Null(result.Receipt);
        Assert.Single(f.Operations.Dispatched);
        // This return gate does not roll back an already-applied operation; exact replay is a Reservations test.
    }

    [Fact]
    public async Task Server_device_establishes_child_scope_property_local_date_and_exact_cursor()
    {
        using var f = new Fixture();
        using var ambient = f.Provider.CreateScope();
        var accessor = ambient.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
        accessor.SetScope(Id(999).ToString("D"));
        var cursor = new StationArrivalCursor(LocalDate.AddDays(-1), "GUEST 42", Id(42));
        var next = new StationArrivalCursor(LocalDate, "GUEST 100", Id(100));
        f.Operations.Page = f.Operations.Page with { Continuation = next };
        var result = await f.Service.ListAsync(Credential, f.Actor, 7, cursor);
        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Equal(Property, result.PropertyId);
        Assert.Equal(LocalDate, result.PropertyLocalDate);
        Assert.NotEqual(DateOnly.FromDateTime(Now.UtcDateTime), result.PropertyLocalDate);
        Assert.Same(next, result.Continuation);
        Assert.Equal((Property, LocalDate, 7, cursor), Assert.Single(f.Operations.ListCalls));
        Assert.All(f.RuntimeScopes, value => Assert.Equal(Tenant, value));
        Assert.NotEmpty(f.RuntimeScopes);
        Assert.Equal(Id(999).ToString("D"), accessor.ScopeId);
        Assert.Same(Assert.Single(f.Operations.Page.Items), Assert.Single(result.Items).Reservation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(26)]
    public async Task Invalid_page_size_never_reads_reservations(int pageSize)
    {
        using var f = new Fixture();
        AssertPageFailure(await f.Service.ListAsync(Credential, f.Actor, pageSize), StationReservationState.Conflict);
        AssertNoProtectedCalls(f);
    }

    [Fact]
    public async Task Twenty_five_complete_arrivals_resolve_exactly_four_bounded_label_batches()
    {
        using var f = new Fixture();
        var arrivals = Enumerable.Range(0, 25).Select(i => Arrival(100 + i,
            Enumerable.Range((i * 4) + 1, 4).Select(Unit).ToArray())).ToArray();
        f.SetArrivals(arrivals);
        var result = await f.ListAsync();
        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Equal(25, result.Items.Count);
        Assert.Equal(4, f.Inventory.Batches.Count);
        Assert.All(f.Inventory.Batches, batch => Assert.Equal(25, batch.Length));
        Assert.Equal(100, f.Inventory.Batches.SelectMany(x => x).Distinct().Count());
        for (int i = 0; i < arrivals.Length; i++)
        {
            Assert.Same(arrivals[i], result.Items[i].Reservation);
            Assert.Equal(arrivals[i].Units.Select(x => x.InventoryUnitId), result.Items[i].Places.Select(x => x.InventoryUnitId));
        }
    }

    [Fact]
    public async Task First_hundred_unit_reservation_remains_whole_and_exact_repeated_units_are_deduplicated_for_lookup()
    {
        using var f = new Fixture();
        var units = Enumerable.Range(1, 100).Select(Unit).Reverse().ToArray();
        f.SetArrivals(Arrival(100, units), Arrival(101, [units[0], units[0], units[99]]));
        var result = await f.ListAsync();
        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Equal(100, result.Items[0].Places.Count);
        Assert.Equal(3, result.Items[1].Places.Count);
        Assert.Equal(units.Select(x => x.InventoryUnitId), result.Items[0].Places.Select(x => x.InventoryUnitId));
        Assert.Equal(units.Select(x => x.InventoryUnitId).Order(), f.Inventory.Batches.SelectMany(x => x));
        Assert.Equal(4, f.Inventory.Batches.Count);
        Assert.All(f.Inventory.Batches, batch => Assert.Equal(25, batch.Length));
    }

    [Theory]
    [InlineData("too-many-reservations")]
    [InlineData("no-units")]
    [InlineData("oversized-reservation")]
    [InlineData("too-many-distinct-units")]
    [InlineData("empty-unit-id")]
    [InlineData("duplicate-unit-version")]
    [InlineData("duplicate-unit-context")]
    public async Task Incomplete_or_unbounded_owner_page_never_partially_resolves_inventory(string scenario)
    {
        using var f = new Fixture();
        var unit = Unit(1);
        f.SetArrivals(scenario switch
        {
            "too-many-reservations" => Enumerable.Range(1, 26).Select(i => Arrival(100 + i, [unit])).ToArray(),
            "no-units" => [Arrival(100, [])],
            "oversized-reservation" => [Arrival(100, Enumerable.Repeat(unit, 101).ToArray())],
            "too-many-distinct-units" => [Arrival(100, Enumerable.Range(1, 100).Select(Unit).ToArray()), Arrival(101, [Unit(101)])],
            "empty-unit-id" => [Arrival(100, [unit with { InventoryUnitId = Guid.Empty }])],
            "duplicate-unit-version" => [Arrival(100, [unit]), Arrival(101, [unit with { UnitVersion = 18 }])],
            "duplicate-unit-context" => [Arrival(100, [unit]), Arrival(101, [unit with { RoomId = Id(800) }])],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
        AssertPageFailure(await f.ListAsync(), StationReservationState.Incomplete);
        Assert.Empty(f.Inventory.Batches);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("room")]
    [InlineData("bed")]
    [InlineData("kind")]
    [InlineData("availability-version")]
    [InlineData("configuration-version")]
    [InlineData("unit-source")]
    [InlineData("room-source")]
    [InlineData("bed-source-missing")]
    [InlineData("bed-source-zero")]
    [InlineData("room-name")]
    [InlineData("bed-label")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("wrong-unit")]
    public async Task Labels_require_exact_complete_identity_and_separate_current_version_coordinates(string scenario)
    {
        using var f = new Fixture();
        f.SetArrivals(Arrival(100, [Unit(1), Unit(2)]));
        f.Inventory.Transform = (_, labels) =>
        {
            var first = labels[0];
            if (scenario == "missing")
            { return labels.Skip(1).ToArray(); }
            if (scenario == "extra")
            { return [.. labels, Label(Unit(3))]; }
            if (scenario == "duplicate")
            { return [first, first]; }
            var changed = scenario switch
            {
                "property" => first with { PropertyId = Id(900) },
                "room" => first with { RoomId = Id(900) },
                "bed" => first with { BedId = Id(900) },
                "kind" => first with { Kind = InventoryUnitKind.Room },
                "availability-version" => first with { UnitVersion = first.ConfigurationVersion },
                "configuration-version" => first with { ConfigurationVersion = first.UnitVersion },
                "unit-source" => first with { UnitSourceVersion = 0 },
                "room-source" => first with { RoomSourceVersion = 0 },
                "bed-source-missing" => first with { BedSourceVersion = null },
                "bed-source-zero" => first with { BedSourceVersion = 0 },
                "room-name" => first with { RoomName = " " },
                "bed-label" => first with { BedLabel = " " },
                "wrong-unit" => first with { InventoryUnitId = Id(900) },
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            return [changed, labels[1]];
        };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Incomplete);
        Assert.Equal(StationReservationState.Incomplete, (await f.CheckInAsync()).State);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task Failed_second_label_batch_discards_every_reservation_and_earlier_label()
    {
        using var f = new Fixture();
        f.SetArrivals(Arrival(100, Enumerable.Range(1, 26).Select(Unit).ToArray()));
        f.Inventory.Transform = (batch, labels) => batch == 2 ? [] : labels;
        AssertPageFailure(await f.ListAsync(), StationReservationState.Incomplete);
        Assert.Equal(2, f.Inventory.Batches.Count);
    }

    [Theory]
    [InlineData(StationInventoryLabelsState.Incomplete, StationReservationState.Incomplete)]
    [InlineData(StationInventoryLabelsState.Invalid, StationReservationState.Incomplete)]
    [InlineData(StationInventoryLabelsState.Unavailable, StationReservationState.Unavailable)]
    public async Task Noncurrent_inventory_results_do_not_expose_plausible_fallback_labels(
        StationInventoryLabelsState inventoryState, StationReservationState expected)
    {
        using var f = new Fixture();
        f.Inventory.State = inventoryState;
        AssertPageFailure(await f.ListAsync(), expected);
        Assert.Equal(expected, (await f.CheckInAsync()).State);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task Empty_ready_page_retains_server_context_without_inventory_lookup()
    {
        using var f = new Fixture();
        f.SetArrivals();
        var result = await f.ListAsync();
        Assert.Equal(StationReservationState.Ready, result.State);
        Assert.Empty(result.Items);
        Assert.Equal(Property, result.PropertyId);
        Assert.Equal(LocalDate, result.PropertyLocalDate);
        Assert.Empty(f.Inventory.Batches);
    }

    [Theory]
    [InlineData(StationReservationState.Denied)]
    [InlineData(StationReservationState.Unavailable)]
    [InlineData(StationReservationState.Unsupported)]
    [InlineData(StationReservationState.Incomplete)]
    [InlineData(StationReservationState.Conflict)]
    public async Task Reservation_failure_state_does_not_leak_owner_items_or_continue(StationReservationState state)
    {
        using var f = new Fixture();
        f.Operations.Page = f.Operations.Page with { State = state, Continuation = new(LocalDate, "PRIVATE", Id(999)) };
        f.Operations.Preparation = f.Operations.Preparation with { State = state };
        AssertPageFailure(await f.ListAsync(), state);
        Assert.Equal(state, (await f.CheckInAsync()).State);
        Assert.Empty(f.Inventory.Batches);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Theory]
    [InlineData(StationAuthorityKind.LinkedStation, StationReservationAuthority.LinkedStation)]
    [InlineData(StationAuthorityKind.StationOnly, StationReservationAuthority.StationOnly)]
    public async Task Selected_reservation_is_prepared_directly_with_server_context_and_exact_provenance(
        StationAuthorityKind kind, StationReservationAuthority reservationKind)
    {
        using var f = new Fixture(kind);
        var selected = Arrival(999, [Unit(80) with { BedId = null, Kind = (int)InventoryUnitKind.Room }]);
        f.Operations.Preparation = new(StationReservationState.Ready, LocalDate, selected);
        f.Operations.Result = new(StationReservationState.Applied,
            new(selected.ReservationId, Property, ReservationStatus.CheckedIn, 2, selected.ExpectedVersion + 1));
        f.Inventory.Labels = selected.Units.Select(Label).ToArray();
        var result = await f.CheckInAsync(reservationId: selected.ReservationId, expectedVersion: selected.ExpectedVersion);
        Assert.Same(f.Operations.Result, result);
        Assert.Equal(StationReservationState.Applied, result.State);
        Assert.Empty(f.Operations.ListCalls);
        var prepared = Assert.Single(f.Operations.Prepared);
        Assert.Equal(Property, prepared.PropertyId);
        Assert.Equal(LocalDate, prepared.Date);
        Assert.Equal(selected.ReservationId, prepared.ReservationId);
        Assert.Equal(Operation, prepared.OperationId);
        Assert.Equal(selected.ExpectedVersion, prepared.Version);
        var provenance = new StationCheckInProvenance(f.Device!.StationId, f.Device.BrowserSessionId,
            Staff, f.Actor.ActorSessionId, f.Actor.Generation, reservationKind,
            kind == StationAuthorityKind.StationOnly ? f.CheckInGrant!.Revision : null);
        Assert.Equal(provenance, prepared.Provenance);
        var dispatched = Assert.Single(f.Operations.Dispatched);
        Assert.Equal(prepared with { Date = f.Operations.Preparation.BusinessDate }, dispatched);
        Assert.Same(f.Operations.Preparation, f.Operations.LastDispatchedPreparation);
        Assert.Equal(selected.Units.Select(x => x.InventoryUnitId), Assert.Single(f.Inventory.Batches));
        if (kind == StationAuthorityKind.StationOnly)
        { Assert.Equal(0, f.LinkedReads); }
        else
        { Assert.True(f.LinkedReads > 0); }
    }

    [Fact]
    public async Task Ready_nonreplay_preparation_without_complete_arrival_is_not_dispatched()
    {
        using var f = new Fixture();
        f.Operations.Preparation = new(StationReservationState.Ready, LocalDate);
        Assert.Equal(StationReservationState.Incomplete, (await f.CheckInAsync()).State);
        Assert.Empty(f.Inventory.Batches);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task Exact_owner_replay_keeps_original_business_date_and_skips_new_allocation_label_lookup()
    {
        using var f = new Fixture();
        f.Operations.Preparation = new(StationReservationState.Ready, LocalDate.AddDays(-1), Replay: true);
        f.Operations.Result = new(StationReservationState.Applied,
            new(Id(100), Property, ReservationStatus.CheckedOut, 2, 14));
        Assert.Same(f.Operations.Result, await f.CheckInAsync());
        Assert.Empty(f.Operations.ListCalls);
        Assert.Empty(f.Inventory.Batches);
        Assert.Equal(LocalDate, Assert.Single(f.Operations.Prepared).Date);
        Assert.Equal(LocalDate.AddDays(-1), Assert.Single(f.Operations.Dispatched).Date);
        Assert.Same(f.Operations.Preparation, f.Operations.LastDispatchedPreparation);
        Assert.True(f.LinkedReads >= 4);
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("runtime")]
    [InlineData("staff")]
    [InlineData("property")]
    [InlineData("linked")]
    [InlineData("list")]
    [InlineData("labels")]
    public async Task Dependency_failure_returns_unavailable_without_partial_guest_data(string boundary)
    {
        using var f = new Fixture();
        f.OnEvent = name => { if (name == boundary) { throw new InvalidOperationException("Synthetic owner unavailable"); } };
        AssertPageFailure(await f.ListAsync(), StationReservationState.Unavailable);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_successful_empty_queue_or_unavailable()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.OnEvent = name =>
        {
            if (name == "list")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ListAsync(Credential, f.Actor,
            cancellationToken: cancellation.Token));
        Assert.Empty(f.Inventory.Batches);
        Assert.Empty(f.Operations.Dispatched);
    }

    [Fact]
    public void Queue_contract_contains_only_minimized_reservation_and_place_fields()
    {
        Assert.Equal(ReservationFieldNames, typeof(StationDueArrival).GetProperties().Select(x => x.Name).Order());
        Assert.Equal(ArrivalFieldNames, typeof(StationFirstJobArrival).GetProperties().Select(x => x.Name).Order());
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static StationAllocationUnit Unit(int value) => new(Id(1000 + value), Id(2000 + value), Id(3000 + value),
        (int)InventoryUnitKind.Bed, 3, 17);
    private static StationDueArrival Arrival(int value, IReadOnlyList<StationAllocationUnit> units) => new(Id(value),
        $"Synthetic guest {value}", LocalDate.AddDays(-1), LocalDate.AddDays(2), 12, Id(4000 + value), 8, units);
    private static StationInventoryLabel Label(StationAllocationUnit unit) => new(Property, unit.InventoryUnitId,
        (InventoryUnitKind)unit.Kind, unit.RoomId, "Garden dorm", unit.BedId, unit.BedId is null ? null : "Upper bunk",
        unit.ConfigurationVersion, unit.UnitVersion, unit.BedId is null ? 21 : 19, 21, unit.BedId is null ? null : 19);
    private static void AssertPageFailure(StationFirstJobPage page, StationReservationState expected)
    {
        Assert.Equal(expected, page.State);
        Assert.Empty(page.Items);
        Assert.Null(page.PropertyId);
        Assert.Null(page.PropertyLocalDate);
        Assert.Null(page.Continuation);
    }
    private static void AssertNoProtectedCalls(Fixture fixture)
    {
        Assert.Empty(fixture.Operations.ListCalls);
        Assert.Empty(fixture.Operations.Prepared);
        Assert.Empty(fixture.Operations.Dispatched);
        Assert.Empty(fixture.Inventory.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_display_uses_stable_owner_names_and_only_names_active_staff(bool active)
    {
        using var f = new Fixture();
        if (!active)
        { f.Facts = f.Facts! with { ActorCurrent = false, Session = f.Facts.Session with { Actor = null } }; }
        StationCurrentView view = await f.ViewAsync();
        Assert.Equal(active ? StationSessionState.Active : StationSessionState.Locked, view.Runtime.State);
        Assert.Equal("Harbour Hostel", view.PropertyName);
        Assert.Equal("Reception", view.Runtime.Session!.StationLabel);
        Assert.Equal(active ? "Alex Reception" : null, view.StaffDisplayName);
        Assert.Equal(active ? 2 : 0, f.Events.Count(e => e == "staff-label"));
    }

    [Theory]
    [InlineData("property-name")]
    [InlineData("station-name")]
    [InlineData("actor")]
    [InlineData("staff-name")]
    [InlineData("staff-removed")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("wrong-staff")]
    [InlineData("zero-version")]
    [InlineData("revoked")]
    [InlineData("exception")]
    public async Task Current_display_suppresses_all_context_when_sources_change_or_fail(string fault)
    {
        using var f = new Fixture();
        int labels = 0;
        f.OnEvent = name =>
        {
            if (name != "staff-label" || ++labels != 2)
            { return; }
            switch (fault)
            {
                case "property-name":
                    f.PropertyFacts = f.PropertyFacts with { Name = "Renamed hostel" };
                    break;
                case "station-name":
                    f.Facts = f.Facts! with { Session = f.Facts.Session with { StationLabel = "Other desk" } };
                    break;
                case "actor":
                    f.Facts = f.Facts! with { Session = f.Facts.Session with { Actor = f.Actor with { ActorSessionId = Id(909), Generation = 8 } } };
                    break;
                case "staff-name":
                    f.StaffLabels = [new(Staff, "Renamed staff", 5)];
                    break;
                case "staff-removed":
                    f.StaffLabels = [];
                    break;
                case "duplicate":
                    f.StaffLabels = [f.StaffLabels[0], f.StaffLabels[0]];
                    break;
                case "blank":
                    f.StaffLabels = [new(Staff, " ", 4)];
                    break;
                case "wrong-staff":
                    f.StaffLabels = [new(Id(909), "Other staff", 4)];
                    break;
                case "zero-version":
                    f.StaffLabels = [new(Staff, "Alex Reception", 0)];
                    break;
                case "revoked":
                    f.Facts = null;
                    break;
                case "exception":
                    throw new InvalidOperationException("Synthetic owner outage");
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault));
            }
        };
        StationCurrentView view = await f.ViewAsync();
        Assert.Equal(fault == "exception" ? StationSessionState.Unavailable : StationSessionState.StateChanged, view.Runtime.State);
        Assert.Null(view.Runtime.Session);
        Assert.Null(view.PropertyName);
        Assert.Null(view.StaffDisplayName);
    }

    private sealed record OperationCall(Guid PropertyId, Guid ReservationId, Guid OperationId, long Version,
        DateOnly Date, StationCheckInProvenance Provenance);

    private sealed class Fixture : IDisposable, IStationCredentialBootstrap, ISystemClock, IStaffStationEligibilitySource,
        IPropertyStationEligibilitySource, IWorkspaceStaffStationAdmissionObserver, IWorkspaceOperationalAdmissionPolicy,
        IWorkspaceTerminationFenceReader, IOrganizationScopeLifecycle, IStaffStationLabelReader, IStationJobGrantReader
    {
        public Fixture(StationAuthorityKind kind = StationAuthorityKind.LinkedStation)
        {
            this.Actor = new(Staff, Id(4), 7, kind);
            this.Device = new(Tenant, Id(5), Id(6), Property);
            this.Handoff = new(this.Device);
            var binding = kind == StationAuthorityKind.LinkedStation
                ? new StationEnrollmentBinding(StationActorKind.LinkedStation, Subject)
                : new StationEnrollmentBinding(StationActorKind.StationOnly, null);
            this.AuthSubject = binding.AuthSubjectId;
            this.Facts = new(new(this.Device.StationId, Property, this.Device.BrowserSessionId, this.Actor.Generation,
                this.Actor, Now.AddDays(2), Now.AddMinutes(10), Now.AddHours(8), "Reception"), true, new(Staff, 2, false, binding), null, true, RegistrationRevision: 1);
            this.Operations = new(this);
            this.Inventory = new(this);
            this.Departures = new(this);
            this.SetArrivals(Arrival(100, [Unit(1)]));
            var services = new ServiceCollection();
            this.Handoff.Register(services);
            services.AddScoped<TestScope>();
            services.AddScoped<IScopeContextAccessor>(sp => sp.GetRequiredService<TestScope>());
            services.AddScoped<IScopeContext>(sp => sp.GetRequiredService<TestScope>());
            services.AddScoped<IStationRuntimeStore>(sp => new RuntimeStore(this, sp.GetRequiredService<IScopeContext>()));
            services.AddScoped(sp => new StationAdmissionCoordinator(sp.GetRequiredService<IScopeContext>(), this,
                this, this, this, this, this, this));
            services.AddSingleton<IStationReservationOperations>(this.Operations);
            services.AddSingleton<IStationDepartureOperations>(this.Departures);
            services.AddSingleton<IStationJobGrantReader>(this);
            services.AddSingleton<IStationInventoryLabelReader>(this.Inventory);
            services.AddSingleton<IPropertyStationEligibilitySource>(this);
            services.AddSingleton<IStaffStationLabelReader>(this);
            this.Provider = services.BuildServiceProvider();
            this.Service = new(this.Provider.GetRequiredService<IServiceScopeFactory>(), this, this);
        }

        public StationActorCoordinate Actor { get; }
        public HandoffOwners Handoff { get; }
        public StationDeviceReference? Device { get; set; }
        public StationRuntimeFacts? Facts { get; set; }
        public ServiceProvider Provider { get; }
        public StationFirstJobService Service { get; }
        public ReservationOwner Operations { get; }
        public InventoryOwner Inventory { get; }
        public DepartureOwner Departures { get; }
        public StationJobGrant? CheckInGrant { get; set; } = new(3, false);
        public StationJobGrant? CheckOutGrant { get; set; }
        public Dictionary<WorkspaceStaffStationAction, WorkspaceStaffStationObservationStatus> JobStatus { get; } = [];
        public List<WorkspaceStaffStationAction> Actions { get; } = [];
        public DateTimeOffset UtcNow { get; private set; } = Now;
        public PropertyStationEligibilitySnapshot PropertyFacts { get; set; } = new(Tenant, Property,
            PropertyStatus.Active, 2, PropertyProcessingStatus.Enabled, "Europe/Helsinki", "Harbour Hostel");
        public IReadOnlyList<StaffStationLabel> StaffLabels { get; set; } = [new(Staff, "Alex Reception", 4)];
        public StaffStatus StaffStatus { get; set; } = StaffStatus.Active;
        public string? AuthSubject { get; set; }
        public WorkspaceOperationalAdmissionDecision Workspace { get; set; } = WorkspaceOperationalAdmissionDecision.Allowed;
        public OrganizationScopeStatus OrganizationStatus { get; set; } = OrganizationScopeStatus.Open;
        public WorkspaceStaffStationObservationStatus LinkedStatus { get; set; } = WorkspaceStaffStationObservationStatus.LinkedAccountPrerequisitesObserved;
        public List<string> Events { get; } = [];
        public List<string?> RuntimeScopes { get; } = [];
        public int BootstrapReads { get; private set; }
        public int LinkedReads { get; private set; }
        public Action<string>? OnEvent { get; set; }
        public void Dispose() => this.Provider.Dispose();
        public void Record(string name) { this.Events.Add(name); this.OnEvent?.Invoke(name); }
        public Task<StationFirstJobPage> ListAsync() => this.Service.ListAsync(Credential, this.Actor);
        public Task<StationCurrentView> ViewAsync() => new StationRuntimeService(
            this.Provider.GetRequiredService<IServiceScopeFactory>(), this, this).ReadViewAsync(Credential);
        public Task<IReadOnlyList<StaffStationLabel>> ResolveAsync(string scopeId, Guid propertyId,
            IReadOnlyCollection<Guid> staffMemberIds, DateOnly localDate, CancellationToken cancellationToken = default)
        {
            this.Record("staff-label");
            Assert.Equal(Tenant, scopeId);
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, Assert.Single(staffMemberIds));
            Assert.Equal(LocalDate, localDate);
            return Task.FromResult(this.StaffLabels);
        }
        public Task<StationCheckInResult> CheckInAsync(string credential = Credential, StationActorCoordinate? actor = null,
            Guid? reservationId = null, long expectedVersion = 12) => this.Service.CheckInAsync(credential,
                actor ?? this.Actor, Operation, reservationId ?? Id(100), expectedVersion);
        public void SetArrivals(params StationDueArrival[] arrivals)
        {
            this.Operations.Page = new(StationReservationState.Ready, arrivals);
            this.Operations.Preparation = new(StationReservationState.Ready, LocalDate, arrivals.FirstOrDefault());
            this.Inventory.Labels = arrivals.SelectMany(x => x.Units).DistinctBy(x => x.InventoryUnitId).Select(Label).ToArray();
        }
        public void ChangeAuthority(string change)
        {
            switch (change)
            {
                case "actor":
                    this.Facts = this.Facts! with { ActorCurrent = false };
                    break;
                case "permission":
                    this.LinkedStatus = WorkspaceStaffStationObservationStatus.PrerequisitesNotObserved;
                    break;
                case "date":
                    this.UtcNow = this.UtcNow.AddDays(1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change));
            }
        }
        public Task<StationJobGrant?> ReadJobGrantAsync(Guid propertyId, Guid staffId, bool checkOut, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, staffId);
            this.Record(checkOut ? "checkout-grant" : "checkin-grant");
            return Task.FromResult(checkOut ? this.CheckOutGrant : this.CheckInGrant);
        }
        public Task<StationDeviceReference?> FindAsync(string opaqueCredential, CancellationToken cancellationToken = default)
        {
            this.BootstrapReads++;
            this.Record("bootstrap");
            Assert.Equal(Credential, opaqueCredential);
            return Task.FromResult(this.Device);
        }
        public Task<StaffStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, Guid staffMemberId,
            CancellationToken cancellationToken = default)
        {
            this.Record("staff");
            Assert.Equal(Tenant, scopeId);
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, staffMemberId);
            return Task.FromResult<StaffStationEligibilitySnapshot?>(new(Tenant, Property, Staff, this.StaffStatus, 4,
                this.AuthSubject is null ? StaffStationAuthLinkState.Unlinked : StaffStationAuthLinkState.Linked, this.AuthSubject,
                StaffStationAssignmentState.Open, new(Id(7), Property, 2, LocalDate.AddDays(-10)),
                StaffProcessingRestrictionGateResult.Allowed(StaffProcessingRestrictionContract.CurrentVersion, 2)));
        }
        public Task<PropertyStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, CancellationToken cancellationToken = default)
        {
            this.Record("property");
            Assert.Equal(Tenant, scopeId);
            Assert.Equal(Property, propertyId);
            return Task.FromResult<PropertyStationEligibilitySnapshot?>(this.PropertyFacts);
        }
        public Task<WorkspaceStaffStationObservation> ObserveAsync(string scopeId, Guid propertyId, Guid staffMemberId,
            WorkspaceStaffStationAction action, CancellationToken cancellationToken = default)
        {
            this.LinkedReads++;
            this.Record("linked");
            Assert.Equal(Tenant, scopeId);
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, staffMemberId);
            this.Actions.Add(action);
            return Task.FromResult(new WorkspaceStaffStationObservation(this.JobStatus.GetValueOrDefault(action, this.LinkedStatus),
                WorkspaceStaffStationObservationReason.LinkedAccountPrerequisites, this.UtcNow,
                DateOnly.FromDateTime(this.UtcNow.UtcDateTime.AddHours(3)), "Europe/Helsinki",
                TimeZoneCatalog.Default.CatalogVersion, TimeZoneCatalog.Default.TzdbVersion));
        }
        public ValueTask<WorkspaceOperationalAdmissionDecision> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Tenant, tenantId);
            return ValueTask.FromResult(this.Workspace);
        }
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            Assert.Equal(Guid.Parse(Tenant), organizationId);
            return Task.FromResult(new OrganizationScopeSnapshot(this.OrganizationStatus, 1));
        }
        public Task<OrganizationScopeExportPage> ExportAsync(OrganizationScopeExportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No export is part of a station first job.");
        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(OrganizationScopeDestroyRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No destruction is part of a station first job.");
    }

    private sealed class RuntimeStore(Fixture fixture, IScopeContext scope) : IStationRuntimeStore
    {
        public Task<StationRuntimeFacts?> ReadAsync(StationDeviceReference device, string opaqueCredential, Guid? selectedStaff,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            fixture.Record("runtime");
            fixture.RuntimeScopes.Add(scope.ScopeId);
            Assert.Equal(Tenant, scope.ScopeId);
            Assert.Equal(fixture.Device, device);
            Assert.Equal(Credential, opaqueCredential);
            Assert.Null(selectedStaff);
            Assert.Equal(fixture.UtcNow, now);
            return Task.FromResult(fixture.Facts);
        }
        public Task<StationSetupFacts?> ReadSetupAsync(StationDeviceReference device, Guid setupId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected setup read.");
        public Task<StationCoreResult?> ReadSetupOutcomeAsync(Guid operationId, StationSetupFacts setup, StationDeviceReference device,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected setup outcome.");
        public Task<StationCoreResult> ForegroundActivityAsync(Guid operationId, StationDeviceReference device, string opaqueCredential,
            StationActorCoordinate actor, StationCredentialFacts credential, long? grantRevision, DateTimeOffset now,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No implicit activity refresh.");
    }

    private sealed class ReservationOwner(Fixture fixture) : IStationReservationOperations
    {
        public StationDueArrivalPage Page { get; set; } = new(StationReservationState.Ready, []);
        public StationCheckInPreparation Preparation { get; set; } = new(StationReservationState.Ready, LocalDate);
        public StationCheckInResult Result { get; set; } = new(StationReservationState.Applied,
            new(Id(100), Property, ReservationStatus.CheckedIn, 2, 13));
        public List<(Guid PropertyId, DateOnly Date, int Size, StationArrivalCursor? Cursor)> ListCalls { get; } = [];
        public List<OperationCall> Prepared { get; } = [];
        public List<OperationCall> Dispatched { get; } = [];
        public StationCheckInPreparation? LastDispatchedPreparation { get; private set; }
        public Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
            StationArrivalCursor? after, CancellationToken cancellationToken = default)
        {
            this.ListCalls.Add((propertyId, localDate, pageSize, after));
            fixture.Record("list");
            return Task.FromResult(this.Page);
        }
        public Task<StationCheckInPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
            long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
        {
            this.Prepared.Add(new(propertyId, reservationId, operationId, expectedVersion, localDate, provenance));
            fixture.Record("prepare");
            return Task.FromResult(this.Preparation);
        }
        public Task<StationCheckInResult> CheckInAsync(Guid propertyId, Guid reservationId, Guid operationId, long expectedVersion,
            StationCheckInPreparation preparation, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
        {
            this.Dispatched.Add(new(propertyId, reservationId, operationId, expectedVersion, preparation.BusinessDate, provenance));
            this.LastDispatchedPreparation = preparation;
            fixture.Record("dispatch");
            return Task.FromResult(this.Result);
        }
    }

    private sealed class InventoryOwner(Fixture fixture) : IStationInventoryLabelReader
    {
        public IReadOnlyList<StationInventoryLabel> Labels { get; set; } = [];
        public StationInventoryLabelsState State { get; set; } = StationInventoryLabelsState.Current;
        public Func<int, IReadOnlyList<StationInventoryLabel>, IReadOnlyList<StationInventoryLabel>>? Transform { get; set; }
        public List<Guid[]> Batches { get; } = [];
        public Task<StationInventoryLabels> ReadAsync(Guid propertyId, IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Property, propertyId);
            Assert.InRange(inventoryUnitIds.Count, 1, 25);
            Assert.Equal(inventoryUnitIds.Count, inventoryUnitIds.Distinct().Count());
            this.Batches.Add(inventoryUnitIds.ToArray());
            fixture.Record("labels");
            IReadOnlyList<StationInventoryLabel> items = this.Labels.Where(x => inventoryUnitIds.Contains(x.InventoryUnitId)).Reverse().ToArray();
            items = this.Transform?.Invoke(this.Batches.Count, items) ?? items;
            return Task.FromResult(new StationInventoryLabels(this.State, items));
        }
    }

    private sealed class TestScope : IScopeContextAccessor
    {
        public bool IsEnabled { get; private set; }
        public string? ScopeId { get; private set; }
        public void SetScope(string scopeId) { this.IsEnabled = true; this.ScopeId = scopeId; }
        public void ClearScope() { this.IsEnabled = false; this.ScopeId = null; }
    }
}
