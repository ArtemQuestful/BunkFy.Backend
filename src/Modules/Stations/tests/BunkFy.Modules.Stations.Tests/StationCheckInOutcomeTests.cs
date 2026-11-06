namespace BunkFy.Modules.Stations.Tests;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Real Stations pairing/property orchestration; explicit read-owner doubles, not HTTP/PostgreSQL proof.</summary>
public sealed class StationCheckInOutcomeTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private const string Credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_paired_device_can_resolve_original_outcome_after_actor_change_or_lock(bool locked)
    {
        using var fixture = new Fixture();
        if (locked)
        { fixture.Facts = fixture.Facts! with { Session = fixture.Facts.Session with { Actor = null } }; }
        var result = await fixture.Resolve();
        Assert.Equal(StationCheckInOutcomeState.Applied, result.State);
        Assert.Equal(1, fixture.OutcomeReads);
        Assert.Equal(4, fixture.PairingReads);
        Assert.Equal(0, fixture.StaffReads);
        Assert.Equal(0, fixture.LinkedActionReads);
        Assert.Equal(Tenant, fixture.OutcomeScope);
    }

    [Fact]
    public async Task Another_browser_coordinate_stops_before_receipt_owner()
    {
        using var fixture = new Fixture();
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await fixture.Resolve(browser: Id(99))).State);
        Assert.Equal(0, fixture.OutcomeReads);
        Assert.Equal(0, fixture.PairingReads);
    }

    [Fact]
    public async Task Future_actor_generation_stops_before_receipt_owner()
    {
        using var fixture = new Fixture();
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await fixture.Resolve(generation: 10)).State);
        Assert.Equal(0, fixture.OutcomeReads);
    }

    [Theory]
    [InlineData("pairing", false)]
    [InlineData("pairing", true)]
    [InlineData("handoff", false)]
    [InlineData("handoff", true)]
    [InlineData("property", false)]
    [InlineData("property", true)]
    public async Task Lost_pairing_pending_handoff_or_closed_property_suppresses_an_outcome(string change, bool afterRead)
    {
        using var fixture = new Fixture();
        void Change()
        {
            if (change == "pairing")
            { fixture.Facts = null; }
            else if (change == "handoff")
            { fixture.Handoff.Active = true; }
            else
            { fixture.PropertyStatus = PropertyStatus.Retired; }
        }
        if (afterRead)
        { fixture.AfterOutcome = Change; }
        else
        { Change(); }
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await fixture.Resolve()).State);
        Assert.Equal(afterRead ? 1 : 0, fixture.OutcomeReads);
    }

    [Theory]
    [InlineData(StationCheckInOutcomeState.Pending)]
    [InlineData(StationCheckInOutcomeState.Conflict)]
    [InlineData(StationCheckInOutcomeState.Unavailable)]
    public async Task Nonterminal_or_failed_owner_state_is_preserved_without_replaying_mutation(StationCheckInOutcomeState state)
    {
        using var fixture = new Fixture { Outcome = new(state) };
        Assert.Equal(state, (await fixture.Resolve()).State);
        Assert.Equal(1, fixture.OutcomeReads);
        Assert.Equal(0, fixture.StaffReads);
    }

    [Fact]
    public async Task Requested_cancellation_does_not_bootstrap_or_read_owner()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolve(token: cancellation.Token));
        Assert.Equal(0, fixture.BootstrapReads);
        Assert.Equal(0, fixture.OutcomeReads);
    }

    private sealed class Fixture : IDisposable, ISystemClock, IStationCredentialBootstrap, IStationRuntimeStore,
        IPropertyStationEligibilitySource, IStaffStationEligibilitySource, IWorkspaceStaffStationAdmissionObserver,
        IWorkspaceOperationalAdmissionPolicy, IWorkspaceTerminationFenceReader, IOrganizationScopeLifecycle
    {
        public Fixture()
        {
            this.Device = new(Tenant, Id(5), Id(4), Id(1));
            this.Handoff = new(this.Device);
            // The new actor is unrelated; original actor Id(7), generation 7, is never reauthorized here.
            this.Facts = new(new(Id(4), Id(1), Id(5), 9,
                new(Id(80), Id(81), 9, StationAuthorityKind.StationOnly), Now.AddDays(1)),
                false, null, null, true, false);
            var services = new ServiceCollection();
            this.Handoff.Register(services);
            services.AddScoped<Scope>();
            services.AddScoped<IScopeContext>(provider => provider.GetRequiredService<Scope>());
            services.AddScoped<IScopeContextAccessor>(provider => provider.GetRequiredService<Scope>());
            services.AddSingleton<IStationRuntimeStore>(this);
            services.AddScoped(provider => new StationAdmissionCoordinator(provider.GetRequiredService<IScopeContext>(),
                this, this, this, this, this, this, this));
            services.AddScoped<IStationCheckInOutcomeReader>(provider => new OutcomeReader(this, provider.GetRequiredService<IScopeContext>()));
            this.Provider = services.BuildServiceProvider();
            this.Service = new(this.Provider.GetRequiredService<IServiceScopeFactory>(), this, this);
            // No mutation port, PIN verifier, arrivals or inventory reader is registered.
        }
        public StationDeviceReference Device { get; }
        public HandoffOwners Handoff { get; }
        public ServiceProvider Provider { get; }
        public StationFirstJobService Service { get; }
        public DateTimeOffset UtcNow => Now;
        public StationRuntimeFacts? Facts { get; set; }
        public PropertyStatus PropertyStatus { get; set; } = PropertyStatus.Active;
        public StationCheckInOutcome Outcome { get; set; } = new(StationCheckInOutcomeState.Applied);
        public Action? AfterOutcome { get; set; }
        public int BootstrapReads { get; private set; }
        public int PairingReads { get; private set; }
        public int OutcomeReads { get; set; }
        public int StaffReads { get; private set; }
        public int LinkedActionReads { get; private set; }
        public string? OutcomeScope { get; set; }
        public void Dispose() => this.Provider.Dispose();
        public Task<StationCheckInOutcome> Resolve(Guid? browser = null, long generation = 7, CancellationToken token = default) =>
            this.Service.ResolveCheckInOutcomeAsync(Credential, browser ?? Id(5), Id(7), generation, Id(3), Id(2), 6, token);
        public Task<StationDeviceReference?> FindAsync(string opaqueCredential, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Credential, opaqueCredential);
            this.BootstrapReads++;
            return Task.FromResult<StationDeviceReference?>(this.Device);
        }
        public Task<StationRuntimeFacts?> ReadAsync(StationDeviceReference device, string opaqueCredential, Guid? selectedStaff,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Assert.Equal(this.Device, device);
            Assert.Equal(Credential, opaqueCredential);
            Assert.Null(selectedStaff);
            this.PairingReads++;
            return Task.FromResult(this.Facts);
        }
        public Task<PropertyStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PropertyStationEligibilitySnapshot?>(new(Tenant, Id(1), this.PropertyStatus, 2,
                PropertyProcessingStatus.Enabled, "Europe/Helsinki", "Synthetic property"));
        public Task<StaffStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, Guid staffMemberId, CancellationToken cancellationToken = default)
        { this.StaffReads++; throw new InvalidOperationException("Outcome-only recovery must not reauthorize the old staff actor."); }
        public Task<WorkspaceStaffStationObservation> ObserveAsync(string scopeId, Guid propertyId, Guid staffMemberId,
            WorkspaceStaffStationAction action, CancellationToken cancellationToken = default)
        { this.LinkedActionReads++; throw new InvalidOperationException("Outcome-only recovery must not evaluate old check-in authority."); }
        public ValueTask<WorkspaceOperationalAdmissionDecision> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(WorkspaceOperationalAdmissionDecision.Allowed);
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 1));
        public Task<StationSetupFacts?> ReadSetupAsync(StationDeviceReference device, Guid setupId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult?> ReadSetupOutcomeAsync(Guid operationId, StationSetupFacts setup, StationDeviceReference device, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult> ForegroundActivityAsync(Guid operationId, StationDeviceReference device, string opaqueCredential,
            StationActorCoordinate actor, StationCredentialFacts credential, long? grantRevision, DateTimeOffset now,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<OrganizationScopeExportPage> ExportAsync(OrganizationScopeExportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(OrganizationScopeDestroyRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class OutcomeReader(Fixture fixture, IScopeContext scope) : IStationCheckInOutcomeReader
    {
        public Task<StationCheckInOutcome> ResolveAsync(Guid propertyId, Guid stationId, Guid browserSessionId,
            Guid actorSessionId, long generation, Guid reservationId, Guid operationId, long expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.True(scope.IsEnabled);
            Assert.Equal(Tenant, scope.ScopeId);
            Assert.Equal(Id(1), propertyId);
            Assert.Equal(Id(4), stationId);
            Assert.Equal(Id(5), browserSessionId);
            Assert.Equal(Id(7), actorSessionId);
            Assert.Equal(7, generation);
            Assert.Equal(Id(2), reservationId);
            Assert.Equal(Id(3), operationId);
            Assert.Equal(6, expectedVersion);
            fixture.OutcomeReads++;
            fixture.OutcomeScope = scope.ScopeId;
            fixture.AfterOutcome?.Invoke();
            return Task.FromResult(fixture.Outcome);
        }
    }
    private sealed class Scope : IScopeContextAccessor
    {
        public bool IsEnabled { get; private set; }
        public string? ScopeId { get; private set; }
        public void SetScope(string scopeId) { this.ScopeId = scopeId; this.IsEnabled = true; }
        public void ClearScope() { this.ScopeId = null; this.IsEnabled = false; }
    }
}
