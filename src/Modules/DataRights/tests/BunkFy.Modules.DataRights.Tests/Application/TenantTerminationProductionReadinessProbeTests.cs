namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationProductionReadinessProbeTests
{
    private static readonly string Digest = new('a', 64);

    [Fact]
    public async Task Valid_catalog_and_durable_replay_store_are_projected()
    {
        StubReplayStore replayStore = new(
            new("postgresql", true, true, null));
        TenantTerminationProductionReadinessProbe probe = new(
            new StubCatalog(isValid: true),
            replayStore);

        TenantTerminationProductionReadinessEvidence evidence =
            await probe.CheckAsync(["workspaces"], CancellationToken.None);

        Assert.True(evidence.IsCatalogValid);
        Assert.Equal(12, evidence.OwnerCount);
        Assert.Equal(9, evidence.ExportOwnerCount);
        Assert.Equal("workspaces", evidence.TerminalOwnerKey);
        Assert.Equal(Digest, evidence.CatalogSha256);
        Assert.Equal("postgresql", evidence.ReplayStoreProvider);
        Assert.True(evidence.IsReplayStoreReady);
        Assert.True(evidence.IsReplayStoreProductionGrade);
        Assert.Equal(1, replayStore.ReadinessChecks);
    }

    [Fact]
    public async Task Invalid_catalog_fails_closed_before_store_access()
    {
        StubReplayStore replayStore = new(
            new("postgresql", true, true, null));
        TenantTerminationProductionReadinessProbe probe = new(
            new StubCatalog(isValid: false),
            replayStore);

        TenantTerminationProductionReadinessEvidence evidence =
            await probe.CheckAsync(["workspaces"], CancellationToken.None);

        Assert.False(evidence.IsCatalogValid);
        Assert.False(evidence.IsReplayStoreReady);
        Assert.Equal(0, replayStore.ReadinessChecks);
    }

    [Fact]
    public async Task Normal_replay_readiness_return_after_cancellation_is_not_projected()
    {
        using CancellationTokenSource source = new();
        StubReplayStore replayStore = new(
            new("postgresql", true, true, null),
            _ => source.Cancel());
        TenantTerminationProductionReadinessProbe probe = new(
            new StubCatalog(isValid: true),
            replayStore);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.CheckAsync(["workspaces"], source.Token));

        Assert.Equal(1, replayStore.ReadinessChecks);
    }

    private sealed class StubCatalog(bool isValid)
        : ITenantTerminationProductionCatalog
    {
        public Result<TenantTerminationProductionCatalogEvidence> Validate(
            IReadOnlyCollection<string> requiredOwnerKeys) =>
            isValid
                ? Result.Success(new TenantTerminationProductionCatalogEvidence(
                    OwnerCount: 12,
                    ExportOwnerCount: 9,
                    TerminalOwnerKey: "workspaces",
                    CatalogSha256: Digest))
                : Result.Failure<TenantTerminationProductionCatalogEvidence>(
                    new Error(
                        "DataRights.CatalogInvalid",
                        "The catalog is invalid."));
    }

    private sealed class StubReplayStore(
        TenantTerminationReplayStoreReadiness readiness,
        Action<CancellationToken>? afterCheck = null)
        : ITenantTerminationReplayStore
    {
        public int ReadinessChecks { get; private set; }

        public Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken)
        {
            this.ReadinessChecks++;
            afterCheck?.Invoke(cancellationToken);
            return Task.FromResult(readiness);
        }

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayCheckpoint> ReadTrustedCheckpointAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayPage> ReadAfterAsync(
            string tenantId,
            Guid processId,
            TenantTerminationReplayCursor cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
