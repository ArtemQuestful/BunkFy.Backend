namespace BunkFy.Modules.DataRights.Application.Tasks;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Naming;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;

internal interface ITenantTerminationExportRetentionScheduler
{
    Task EnqueueArtifactCleanupAsync(
        string tenantId,
        Guid processId,
        Guid artifactId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    Task EnqueueFragmentCleanupAsync(
        string tenantId,
        Guid processId,
        Guid fragmentId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableTenantTerminationExportRetentionScheduler
    : ITenantTerminationExportRetentionScheduler
{
    public Task EnqueueArtifactCleanupAsync(
        string tenantId,
        Guid processId,
        Guid artifactId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken) => Unavailable(cancellationToken);

    public Task EnqueueFragmentCleanupAsync(
        string tenantId,
        Guid processId,
        Guid fragmentId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken) => Unavailable(cancellationToken);

    private static Task Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new InvalidOperationException(
            UnavailableTenantTerminationTaskScheduler.ErrorCode));
    }
}

internal sealed class TenantTerminationExportRetentionScheduler(
    ITaskRunStore taskRuns,
    ISystemClock clock) : ITenantTerminationExportRetentionScheduler
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public Task EnqueueArtifactCleanupAsync(
        string tenantId,
        Guid processId,
        Guid artifactId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        string normalizedTenantId = RequireTenant(tenantId);
        Guid runId = TenantTerminationExecutionIdentity
            .CreateExportArtifactCleanupTaskRunId(artifactId);
        DeleteExpiredTenantTerminationExportArtifactPayload payload = new(
            normalizedTenantId,
            processId,
            artifactId,
            operationRevision,
            expiresAtUtc);
        return this.EnqueueAsync(
            processId,
            runId,
            DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
            DeleteExpiredTenantTerminationExportArtifactPayload
                .PayloadVersion,
            JsonSerializer.Serialize(payload, SerializerOptions),
            expiresAtUtc,
            cancellationToken);
    }

    public Task EnqueueFragmentCleanupAsync(
        string tenantId,
        Guid processId,
        Guid fragmentId,
        long operationRevision,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        string normalizedTenantId = RequireTenant(tenantId);
        Guid runId = TenantTerminationExecutionIdentity
            .CreateExportFragmentCleanupTaskRunId(fragmentId);
        DeleteExpiredTenantTerminationExportFragmentPayload payload = new(
            normalizedTenantId,
            processId,
            fragmentId,
            operationRevision,
            expiresAtUtc);
        return this.EnqueueAsync(
            processId,
            runId,
            DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
            DeleteExpiredTenantTerminationExportFragmentPayload
                .PayloadVersion,
            JsonSerializer.Serialize(payload, SerializerOptions),
            expiresAtUtc,
            cancellationToken);
    }

    private async Task EnqueueAsync(
        Guid processId,
        Guid runId,
        string taskName,
        int payloadVersion,
        string payloadJson,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (processId == Guid.Empty ||
            runId == Guid.Empty ||
            expiresAtUtc == default)
        {
            throw InvalidSchedule();
        }

        DateTimeOffset createdAtUtc = clock.UtcNow;
        if (createdAtUtc == default)
        {
            throw InvalidSchedule();
        }

        TaskRunRequest request = new(
            runId,
            DataRightsModuleMetadata.Name,
            taskName,
            payloadJson,
            createdAtUtc,
            expiresAtUtc,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            scopeId: null,
            correlationId: processId,
            requestedBy: "system:tenant-termination-export-retention",
            maxAttempts: 10,
            payloadVersion: payloadVersion,
            deduplicationKey: TenantTerminationExecutionIdentity
                .CreateTaskDeduplicationKey(runId));
        TaskRunEnqueueResult result = await taskRuns.EnqueueAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!Matches(request, result?.Run))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationTaskRunConflict");
        }
    }

    private static bool Matches(
        TaskRunRequest request,
        TaskRunDetails? run)
    {
        if (run is null)
        {
            return false;
        }

        TaskRunSummary summary = run.Summary;
        return summary.RunId == request.RunId &&
            string.Equals(
                summary.ModuleName,
                request.ModuleName,
                StringComparison.Ordinal) &&
            string.Equals(
                summary.TaskName,
                request.TaskName,
                StringComparison.Ordinal) &&
            string.Equals(
                summary.WorkerGroup,
                request.WorkerGroup,
                StringComparison.Ordinal) &&
            summary.PayloadVersion == request.PayloadVersion &&
            summary.ScheduledAtUtc == request.ScheduledAtUtc &&
            string.Equals(
                summary.ScopeId,
                request.ScopeId,
                StringComparison.Ordinal) &&
            summary.CorrelationId == request.CorrelationId &&
            summary.MaxAttempts == request.MaxAttempts &&
            string.Equals(
                summary.RequestedBy,
                request.RequestedBy,
                StringComparison.Ordinal) &&
            string.Equals(
                summary.DeduplicationKey,
                request.DeduplicationKey,
                StringComparison.Ordinal) &&
            string.Equals(
                run.PayloadJson,
                request.PayloadJson,
                StringComparison.Ordinal);
    }

    private static InvalidOperationException InvalidSchedule() =>
        new("DataRights.TenantTerminationTaskScheduleInvalid");

    private static string RequireTenant(string tenantId)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? normalizedTenantId) ||
            !string.Equals(
                tenantId,
                normalizedTenantId,
                StringComparison.Ordinal))
        {
            throw InvalidSchedule();
        }

        return normalizedTenantId;
    }
}
