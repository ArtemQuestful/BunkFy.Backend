namespace BunkFy.Modules.Stations.Tests;

using System.Security.Cryptography;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Auth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class StationRuntimeServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    public async Task Noncanonical_credentials_are_invalid_before_bootstrap(string credential)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var bootstrap = new Bootstrap(null);
        var runtime = new StationRuntimeService(services.GetRequiredService<IServiceScopeFactory>(), bootstrap, new Clock());
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(credential));
        var roster = await runtime.RosterAsync(credential);
        Assert.Equal(StationSessionState.Invalid, roster.State);
        Assert.Empty(roster.Items);
        Assert.Equal(0, bootstrap.Reads);
    }

    [Fact]
    public async Task Canonical_random_bytes_are_hashed_and_unknown_lookup_returns_no_coordinates()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string credential = Encode(bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), StationCredentialEncoding.Digest(credential));
        using var services = new ServiceCollection().BuildServiceProvider();
        var bootstrap = new Bootstrap(null);
        var runtime = new StationRuntimeService(services.GetRequiredService<IServiceScopeFactory>(), bootstrap, new Clock());
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(credential));
        Assert.Equal(1, bootstrap.Reads);
    }

    [Fact]
    public async Task Bootstrap_failure_is_unavailable_and_cancellation_is_not_swallowed()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var bootstrap = new Bootstrap(null) { Unavailable = true };
        var runtime = new StationRuntimeService(services.GetRequiredService<IServiceScopeFactory>(), bootstrap, new Clock());
        string credential = Encode(new byte[32]);
        Assert.Equal(new(StationSessionState.Unavailable), await runtime.ReadAsync(credential));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ReadAsync(credential, cancellation.Token));
        Assert.Equal(1, bootstrap.Reads);
    }

    [Fact]
    public async Task Scoped_revalidation_uses_a_new_scope_and_never_overwrites_ambient_or_invokes_owners_after_rejection()
    {
        string discovered = Guid.NewGuid().ToString("D"), ambient = Guid.NewGuid().ToString("D");
        var device = new StationDeviceReference(discovered, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var readScopes = new List<string?>();
        var services = new ServiceCollection();
        services.AddScoped<TestScope>();
        services.AddScoped<IScopeContextAccessor>(sp => sp.GetRequiredService<TestScope>());
        services.AddScoped<IScopeContext>(sp => sp.GetRequiredService<TestScope>());
        services.AddScoped<IStationRuntimeStore>(sp => new InvalidStore(sp.GetRequiredService<IScopeContext>(), readScopes));
        using var provider = services.BuildServiceProvider();
        using var request = provider.CreateScope();
        var accessor = request.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
        accessor.SetScope(ambient);
        var runtime = new StationRuntimeService(request.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), new Bootstrap(device), new Clock());
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(Encode(new byte[32])));
        Assert.Equal(ambient, accessor.ScopeId);
        Assert.Equal(discovered, Assert.Single(readScopes));
        // No coordinator/owners are registered: invalid device revalidation must never resolve them.
    }

    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData("active", StationSessionState.HandoffPending)]
    [InlineData("missing", StationSessionState.StateChanged)]
    [InlineData("wrong-device", StationSessionState.StateChanged)]
    [InlineData("bad-subject", StationSessionState.StateChanged)]
    [InlineData("empty-session", StationSessionState.StateChanged)]
    [InlineData("receipt-unavailable", StationSessionState.Unavailable)]
    [InlineData("session-unavailable", StationSessionState.Unavailable)]
    [InlineData("missing-auth-scope", StationSessionState.Unavailable)]
    public async Task Handoff_blocks_every_runtime_callback_without_disclosing_actor_or_roster(string condition, StationSessionState expected)
    {
        var device = new StationDeviceReference(Guid.NewGuid().ToString("D"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var owners = new HandoffOwners(device);
        switch (condition)
        {
            case "active":
                owners.Active = true;
                break;
            case "missing":
                owners.Receipt = null;
                break;
            case "wrong-device":
                owners.Receipt = owners.Receipt! with { Device = device with { BrowserSessionId = Guid.NewGuid() } };
                break;
            case "bad-subject":
                owners.Receipt = owners.Receipt! with { IssuerSubjectId = "not-an-account" };
                break;
            case "empty-session":
                owners.Receipt = owners.Receipt! with { IssuerSessionId = Guid.Empty };
                break;
            case "receipt-unavailable":
                owners.ReceiptUnavailable = true;
                break;
            case "session-unavailable":
                owners.SessionUnavailable = true;
                break;
            case "missing-auth-scope":
                owners.AuthScope = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }
        var services = new ServiceCollection();
        services.AddScoped<IScopeContextAccessor, TestScope>();
        services.AddSingleton<IStationRuntimeStore>(new LockedStore(device));
        owners.Register(services);
        using var provider = services.BuildServiceProvider();
        var runtime = new StationRuntimeService(provider.GetRequiredService<IServiceScopeFactory>(), new Bootstrap(device), new Clock());
        string secret = Encode(new byte[32]);
        var actor = new StationActorCoordinate(Guid.NewGuid(), Guid.NewGuid(), 1, StationAuthorityKind.StationOnly);
        Assert.Equal(new(expected), await runtime.ReadAsync(secret));
        Assert.Equal(new(new(expected)), await runtime.ReadViewAsync(secret));
        Assert.Equal(new(expected), await runtime.UnlockAsync(secret, Guid.NewGuid(), actor.StaffMemberId, 1, "000001"));
        Assert.Equal(new(expected), await runtime.LockAsync(secret, Guid.NewGuid(), 1));
        Assert.Equal(new(expected), await runtime.ForegroundActivityAsync(secret, Guid.NewGuid(), actor));
        Assert.Equal(new(expected), await runtime.RedeemSeededSetupAsync(secret, Guid.NewGuid(), Guid.NewGuid(), "000001"));
        var roster = await runtime.RosterAsync(secret);
        Assert.Equal(expected, roster.State);
        Assert.Empty(roster.Items);
        // No Staff, property, management, KDF, mutation store or admission services are registered.
        Assert.Equal(condition is "active" or "session-unavailable" ? 7 : 0, owners.SessionReads);
    }

    [Fact]
    public async Task Exact_inactive_original_session_allows_normal_locked_state_and_handoff_cancellation_propagates()
    {
        var device = new StationDeviceReference(Guid.NewGuid().ToString("D"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var owners = new HandoffOwners(device);
        var services = new ServiceCollection();
        services.AddScoped<IScopeContextAccessor, TestScope>();
        services.AddSingleton<IStationRuntimeStore>(new LockedStore(device));
        owners.Register(services);
        using var provider = services.BuildServiceProvider();
        var runtime = new StationRuntimeService(provider.GetRequiredService<IServiceScopeFactory>(), new Bootstrap(device), new Clock());
        var result = await runtime.ReadAsync(Encode(new byte[32]));
        Assert.Equal(StationSessionState.Locked, result.State);
        Assert.NotNull(result.Session);
        Assert.Null(result.Session.Actor);
        Assert.Equal(1, owners.SessionReads);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<StationHandoffAdmission>().ObserveAsync(device, cancelled.Token));
    }

    private sealed class LockedStore(StationDeviceReference device) : IStationRuntimeStore
    {
        public Task<StationRuntimeFacts?> ReadAsync(StationDeviceReference selected, string opaqueCredential, Guid? selectedStaff,
            DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult<StationRuntimeFacts?>(new(
                new(device.StationId, device.PropertyId, device.BrowserSessionId, 1, null, now.AddDays(1)), false, null, null, true));
        public Task<StationSetupFacts?> ReadSetupAsync(StationDeviceReference selected, Guid setupId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No setup callback before handoff.");
        public Task<StationCoreResult?> ReadSetupOutcomeAsync(Guid operationId, StationSetupFacts setup, StationDeviceReference selected, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No setup callback before handoff.");
        public Task<StationCoreResult> ForegroundActivityAsync(Guid operationId, StationDeviceReference selected, string opaqueCredential, StationActorCoordinate actor,
            StationCredentialFacts credential, long? grantRevision, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No activity before handoff.");
    }
    private sealed class Bootstrap(StationDeviceReference? device) : IStationCredentialBootstrap
    {
        public int Reads { get; private set; }
        public bool Unavailable { get; init; }
        public Task<StationDeviceReference?> FindAsync(string opaqueCredential, CancellationToken cancellationToken = default)
        {
            this.Reads++;
            return this.Unavailable ? throw new InvalidOperationException("Synthetic unavailable") : Task.FromResult(device);
        }
    }
    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => StationDomainTests.Now; }
    private sealed class TestScope : IScopeContextAccessor
    {
        public bool IsEnabled { get; private set; }
        public string? ScopeId { get; private set; }
        public void SetScope(string scopeId) { this.IsEnabled = true; this.ScopeId = scopeId; }
        public void ClearScope() { this.IsEnabled = false; this.ScopeId = null; }
    }
    private sealed class InvalidStore(IScopeContext scope, List<string?> reads) : IStationRuntimeStore
    {
        public Task<StationRuntimeFacts?> ReadAsync(StationDeviceReference device, string opaqueCredential, Guid? selectedStaff,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        { reads.Add(scope.ScopeId); return Task.FromResult<StationRuntimeFacts?>(null); }
        public Task<StationSetupFacts?> ReadSetupAsync(StationDeviceReference device, Guid setupId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult?> ReadSetupOutcomeAsync(Guid operationId, StationSetupFacts setup, StationDeviceReference device, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult> ForegroundActivityAsync(Guid operationId, StationDeviceReference device, string opaqueCredential, StationActorCoordinate actor,
            StationCredentialFacts credential, long? grantRevision, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

/// <summary>Explicit fake pairing/Auth owners; the real handoff admission service runs in these unit tests.</summary>
internal sealed class HandoffOwners(StationDeviceReference device) : IStationPairingHandoffReader, IAuthSessionAdmissionReader
{
    public StationPairingHandoffFacts? Receipt { get; set; } = new(device, "cc000000-0000-0000-0000-000000000003", Guid.NewGuid());
    public bool Active { get; set; }
    public bool ReceiptUnavailable { get; set; }
    public bool SessionUnavailable { get; set; }
    public string? AuthScope { get; set; } = "fixture-auth";
    public int SessionReads { get; private set; }
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IStationPairingHandoffReader>(this);
        services.AddSingleton<IAuthSessionAdmissionReader>(this);
        services.AddSingleton<IOptions<StationOptions>>(Options.Create(new StationOptions { ManagementAuthScopeId = this.AuthScope }));
        services.AddScoped<StationHandoffAdmission>();
    }
    public Task<StationPairingHandoffFacts?> ReadHandoffAsync(StationDeviceReference selected, CancellationToken cancellationToken = default)
    {
        Assert.Equal(device, selected);
        return this.ReceiptUnavailable ? throw new InvalidOperationException("Synthetic receipt failure.") : Task.FromResult(this.Receipt);
    }
    public ValueTask<bool> IsActiveAsync(string scopeId, Guid memberId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        this.SessionReads++;
        Assert.Equal(this.AuthScope, scopeId);
        Assert.Equal(Guid.Parse(this.Receipt!.IssuerSubjectId), memberId);
        Assert.Equal(this.Receipt.IssuerSessionId, sessionId);
        return this.SessionUnavailable ? throw new InvalidOperationException("Synthetic Auth failure.") : ValueTask.FromResult(this.Active);
    }
}
