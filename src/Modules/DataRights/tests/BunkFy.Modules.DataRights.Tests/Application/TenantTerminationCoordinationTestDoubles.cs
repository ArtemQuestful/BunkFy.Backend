namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;

internal sealed record TenantTerminationSignalCapture(
    string TenantId,
    Guid ProcessId,
    TenantTerminationProcessPhase Phase,
    TenantTerminationProcessStatus Status,
    long ProcessVersion,
    long OperationRevision,
    DateTimeOffset OccurredAtUtc);

internal sealed class RecordingTenantTerminationCoordinationSignal
    : ITenantTerminationCoordinationSignal
{
    public List<TenantTerminationSignalCapture> Captures { get; } = [];

    public Task<bool> EnqueueAsync(
        TenantTerminationProcess process,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        this.Captures.Add(new(
            process.ScopeId,
            process.Id,
            process.Phase,
            process.Status,
            process.Version,
            process.OperationRevision,
            occurredAtUtc));
        return Task.FromResult(true);
    }
}

internal sealed class RecordingTenantTerminationExportRetentionScheduler
    : ITenantTerminationExportRetentionScheduler
{
    public List<(Guid ProcessId, Guid ArtifactId, long Revision,
        DateTimeOffset ExpiresAtUtc)> Artifacts
    { get; } = [];
    public List<(Guid ProcessId, Guid FragmentId, long Revision,
        DateTimeOffset ExpiresAtUtc)> Fragments
    { get; } = [];

    public Task EnqueueArtifactCleanupAsync(
        string tenantId,
        Guid processId,
        Guid artifactId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        this.Artifacts.Add((
            processId,
            artifactId,
            operationRevision,
            expiresAtUtc));
        return Task.CompletedTask;
    }

    public Task EnqueueFragmentCleanupAsync(
        string tenantId,
        Guid processId,
        Guid fragmentId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        this.Fragments.Add((
            processId,
            fragmentId,
            operationRevision,
            expiresAtUtc));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryTenantTerminationExportArtifactRepository
    : ITenantTerminationExportArtifactRepository
{
    private readonly List<TenantTerminationExportArtifact> artifacts = [];

    public IReadOnlyList<TenantTerminationExportArtifact> Items =>
        this.artifacts;

    public Task AddAsync(
        TenantTerminationExportArtifact artifact,
        CancellationToken cancellationToken)
    {
        this.artifacts.Add(artifact);
        return Task.CompletedTask;
    }

    public Task<TenantTerminationExportArtifact?> GetAsync(
        Guid artifactId,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.artifacts.SingleOrDefault(
            artifact => artifact.Id == artifactId));

    public Task<TenantTerminationExportArtifact?> GetByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.artifacts.SingleOrDefault(
            artifact => artifact.IdempotencyKey == idempotencyKey));

    public Task<TenantTerminationExportArtifact?> GetByProcessAsync(
        Guid processId,
        long exportOperationRevision,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.artifacts.SingleOrDefault(artifact =>
            artifact.ProcessId == processId &&
            artifact.ExportOperationRevision == exportOperationRevision));
}

internal sealed class InMemoryTenantTerminationExportFragmentRepository
    : ITenantTerminationExportFragmentRepository
{
    private readonly List<TenantTerminationExportFragment> fragments = [];

    public IReadOnlyList<TenantTerminationExportFragment> Items =>
        this.fragments;

    public Task AddAsync(
        TenantTerminationExportFragment fragment,
        CancellationToken cancellationToken)
    {
        this.fragments.Add(fragment);
        return Task.CompletedTask;
    }

    public Task<TenantTerminationExportFragment?> GetAsync(
        Guid workItemId,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.fragments.SingleOrDefault(
            fragment => fragment.Id == workItemId));

    public Task<TenantTerminationExportFragment?> GetByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.fragments.SingleOrDefault(
            fragment => fragment.IdempotencyKey == idempotencyKey));

    public Task<IReadOnlyList<TenantTerminationExportFragment>> ListAsync(
        Guid processId,
        long exportOperationRevision,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TenantTerminationExportFragment>>(
            this.fragments.Where(fragment =>
                fragment.ProcessId == processId &&
                fragment.ExportOperationRevision == exportOperationRevision)
                .ToArray());
}

internal sealed class StubTenantTerminationReplayStore
    : ITenantTerminationReplayStore
{
    private readonly Dictionary<
        TenantTerminationReplayAttemptCoordinate,
        TenantTerminationReplayAttempt> attempts = [];

    public void Add(TenantTerminationReplayAttempt attempt) =>
        this.attempts[attempt.Dispatch.Coordinate] = attempt;

    public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
        TenantTerminationReplayAttemptCoordinate coordinate,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.attempts.GetValueOrDefault(coordinate));

    public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
        string tenantId,
        Guid processId,
        CancellationToken cancellationToken) =>
        Task.FromResult<TenantTerminationReplayIntent?>(null);

    public Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
        TenantTerminationReplayJournalEntry entry,
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
