namespace BunkFy.Modules.Reservations.Tests.Application;

using System.Text.Json;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Stations;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StationCheckInOutcomeReaderTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private static readonly DateOnly Day = new(2026, 9, 20);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    [Theory]
    [InlineData(StationReservationAuthority.LinkedStation)]
    [InlineData(StationReservationAuthority.StationOnly)]
    public async Task Exact_persisted_attribution_confirms_only_a_guest_free_state(StationReservationAuthority authority)
    {
        var owner = new Owner();
        owner.Attribution = owner.Attribution with { Provenance = owner.Attribution.Provenance! with { Authority = authority } };
        var result = await Resolve(owner);
        Assert.Equal(StationCheckInOutcomeState.Applied, result.State);
        Assert.Equal("{\"state\":1}", JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        Assert.Equal(["operation", "attribution"], owner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Absent_receipt_remains_pending_even_if_attribution_appears_during_the_read(bool concurrentCommit)
    {
        var owner = new Owner { Receipt = null };
        if (!concurrentCommit)
        { owner.Attribution = new(true); }
        Assert.Equal(StationCheckInOutcomeState.Pending, (await Resolve(owner)).State);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("property")]
    [InlineData("reservation")]
    [InlineData("operation")]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("details")]
    [InlineData("missing-date")]
    [InlineData("default-date")]
    [InlineData("fingerprint")]
    [InlineData("missing-attribution")]
    [InlineData("station")]
    [InlineData("browser")]
    [InlineData("actor")]
    [InlineData("generation")]
    [InlineData("invalid-persisted-staff")]
    [InlineData("invalid-persisted-authority")]
    public async Task Mismatched_or_unattributed_operation_cannot_be_confirmed(string scenario)
    {
        var owner = new Owner();
        var receipt = owner.Receipt!;
        var provenance = owner.Attribution.Provenance!;
        owner.Receipt = scenario switch
        {
            "scope" => receipt with { ScopeId = Id(90).ToString("D") },
            "property" => receipt with { PropertyId = Id(90) },
            "reservation" => receipt with { ReservationId = Id(90) },
            "operation" => receipt with { OperationId = Id(90) },
            "kind" => receipt with { Kind = ReservationManagementOperationKind.CheckOut },
            "version" => receipt with { ExpectedVersion = 8 },
            "details" => receipt with { ExpectedDetailsRevision = 1 },
            "missing-date" => receipt with { BusinessDate = null },
            "default-date" => receipt with { BusinessDate = default(DateOnly) },
            "fingerprint" => receipt with { RequestFingerprint = "unexpected" },
            _ => receipt
        };
        owner.Attribution = new(true, scenario switch
        {
            "missing-attribution" => null,
            "station" => provenance with { StationId = Id(90) },
            "browser" => provenance with { BrowserSessionId = Id(90) },
            "actor" => provenance with { ActorSessionId = Id(90) },
            "generation" => provenance with { Generation = 8 },
            "invalid-persisted-staff" => provenance with { StaffMemberId = Guid.Empty },
            "invalid-persisted-authority" => provenance with { Authority = StationReservationAuthority.Unknown },
            _ => provenance
        });
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await Resolve(owner)).State);
    }

    [Fact]
    public async Task Missing_or_changed_scope_stops_confirmation()
    {
        var owner = new Owner { IsEnabled = false };
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await Resolve(owner)).State);
        Assert.Empty(owner.Calls);
        owner.IsEnabled = true;
        owner.OnAttribution = () => owner.ScopeId = Id(90).ToString("D");
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await Resolve(owner)).State);
    }

    [Fact]
    public async Task Invalid_historical_coordinates_stop_before_owner_reads()
    {
        var owner = new Owner();
        var reader = new StationCheckInOutcomeReader(owner, owner);
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await reader.ResolveAsync(Id(1), Id(4), Id(5),
            Guid.Empty, 7, Id(2), Id(3), 6)).State);
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await reader.ResolveAsync(Id(1), Id(4), Id(5),
            Id(7), 0, Id(2), Id(3), 6)).State);
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await reader.ResolveAsync(Id(1), Id(4), Id(5),
            Id(7), 7, Id(2), Id(3), 0)).State);
        Assert.Empty(owner.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("attribution")]
    public async Task Owner_failure_is_unavailable_not_missing_or_applied(string stage)
    {
        var owner = new Owner { FailAt = stage };
        Assert.Equal(StationCheckInOutcomeState.Unavailable, (await Resolve(owner)).State);
    }

    [Fact]
    public async Task Unsupported_attribution_is_unavailable()
    {
        var owner = new Owner { Attribution = new(false) };
        Assert.Equal(StationCheckInOutcomeState.Unavailable, (await Resolve(owner)).State);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_owner_reads()
    {
        var owner = new Owner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolve(owner, cancellation.Token));
        Assert.Empty(owner.Calls);
    }

    private static Task<StationCheckInOutcome> Resolve(Owner owner, CancellationToken token = default) =>
        new StationCheckInOutcomeReader(owner, owner).ResolveAsync(Id(1), Id(4), Id(5), Id(7), 7, Id(2), Id(3), 6, token);

    private sealed class Owner : IReservationManagementOperationRepository, IScopeContext
    {
        public bool IsEnabled { get; set; } = true;
        public string ScopeId { get; set; } = Tenant;
        public string? FailAt { get; init; }
        public Action? OnAttribution { get; set; }
        public List<string> Calls { get; } = [];
        public ReservationManagementOperationRecord? Receipt { get; set; } = new(Id(3), Tenant, Id(1), Id(2),
            ReservationManagementOperationKind.CheckIn, 6, null, Day, new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        public ReservationStationAttributionRead Attribution { get; set; } = new(true,
            new(Id(4), Id(5), Id(6), Id(7), 7, StationReservationAuthority.LinkedStation));
        public Task<ReservationManagementOperationRecord?> GetAsync(Guid reservationId, Guid operationId, CancellationToken cancellationToken)
        {
            Assert.Equal(Id(2), reservationId);
            Assert.Equal(Id(3), operationId);
            this.Calls.Add("operation");
            if (this.FailAt == "operation")
            { throw new InvalidOperationException("Synthetic receipt failure"); }
            return Task.FromResult(this.Receipt);
        }
        public Task<ReservationStationAttributionRead> GetStationAttributionAsync(Guid reservationId, Guid operationId, CancellationToken cancellationToken)
        {
            Assert.Equal(Id(2), reservationId);
            Assert.Equal(Id(3), operationId);
            this.Calls.Add("attribution");
            this.OnAttribution?.Invoke();
            if (this.FailAt == "attribution")
            { throw new InvalidOperationException("Synthetic attribution failure"); }
            return Task.FromResult(this.Attribution);
        }
        public Task AddAsync(ReservationManagementOperationRecord operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Outcome resolver must never add an operation.");
        public Task AddStationAsync(ReservationManagementOperationRecord operation, StationCheckInProvenance provenance,
            long resultingVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Outcome resolver must never add station attribution.");
    }
}
