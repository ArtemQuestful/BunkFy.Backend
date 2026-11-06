namespace BunkFy.Host.Worker;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Naming;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;

internal sealed class TenantTerminationGlobalTaskExecutionContextContributor(
    ITenantContextAccessor tenantContext)
    : ITaskExecutionContextContributor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private bool established;

    public ValueTask<TaskExecutionContextPreparationResult> PrepareAsync(
        TaskExecutionContextPreparationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.established = false;
        if (!IsSupportedRegistration(context))
        {
            return ValueTask.FromResult(
                TaskExecutionContextPreparationResult.Success());
        }

        if (!MatchesLease(context) ||
            !TryReadCoordinates(context, out GlobalTenantCoordinates coordinates) ||
            !TryTenantId(coordinates.TenantId, out string tenantId) ||
            context.Lease.ScopeId is not null ||
            context.Lease.CorrelationId != coordinates.ProcessId)
        {
            return ValueTask.FromResult(
                TaskExecutionContextPreparationResult.Failure(
                    "Global tenant-termination task coordinates are invalid."));
        }

        tenantContext.SetTenant(tenantId);
        this.established = true;
        return ValueTask.FromResult(
            TaskExecutionContextPreparationResult.Success());
    }

    public ValueTask CleanupAsync(
        TaskExecutionContextPreparationContext context,
        CancellationToken cancellationToken)
    {
        if (this.established)
        {
            tenantContext.ClearTenant();
            this.established = false;
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsSupportedRegistration(
        TaskExecutionContextPreparationContext context) =>
        !context.Registration.IsTenantScoped() &&
        string.Equals(
            context.Registration.ModuleName,
            DataRightsModuleMetadata.Name,
            StringComparison.Ordinal) &&
        string.Equals(
            context.Registration.WorkerGroup,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            StringComparison.Ordinal) &&
        IsSupportedPayload(
            context.Registration.PayloadType,
            context.Registration.TaskName,
            context.Registration.PayloadVersion);

    private static bool MatchesLease(
        TaskExecutionContextPreparationContext context) =>
        string.Equals(
            context.Lease.ModuleName,
            context.Registration.ModuleName,
            StringComparison.Ordinal) &&
        string.Equals(
            context.Lease.WorkerGroup,
            context.Registration.WorkerGroup,
            StringComparison.Ordinal) &&
        string.Equals(
            context.Lease.TaskName,
            context.Registration.TaskName,
            StringComparison.Ordinal) &&
        context.Lease.PayloadVersion == context.Registration.PayloadVersion;

    private static bool IsSupportedPayload(
        Type payloadType,
        string taskName,
        int payloadVersion) =>
        MatchesPayload<ExecuteGlobalTenantTerminationOwnerWorkPayload>(
            payloadType,
            taskName,
            payloadVersion,
            ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName,
            ExecuteGlobalTenantTerminationOwnerWorkPayload.PayloadVersion) ||
        MatchesPayload<DeleteExpiredTenantTerminationExportArtifactPayload>(
            payloadType,
            taskName,
            payloadVersion,
            DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
            DeleteExpiredTenantTerminationExportArtifactPayload
                .PayloadVersion) ||
        MatchesPayload<DeleteExpiredTenantTerminationExportFragmentPayload>(
            payloadType,
            taskName,
            payloadVersion,
            DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
            DeleteExpiredTenantTerminationExportFragmentPayload
                .PayloadVersion);

    private static bool MatchesPayload<TPayload>(
        Type payloadType,
        string taskName,
        int payloadVersion,
        string expectedTaskName,
        int expectedPayloadVersion) =>
        payloadType == typeof(TPayload) &&
        string.Equals(
            taskName,
            expectedTaskName,
            StringComparison.Ordinal) &&
        payloadVersion == expectedPayloadVersion;

    private static bool TryReadCoordinates(
        TaskExecutionContextPreparationContext context,
        out GlobalTenantCoordinates coordinates)
    {
        coordinates = default;
        try
        {
            if (context.Registration.PayloadType ==
                typeof(ExecuteGlobalTenantTerminationOwnerWorkPayload))
            {
                ExecuteGlobalTenantTerminationOwnerWorkPayload? payload =
                    JsonSerializer.Deserialize<
                        ExecuteGlobalTenantTerminationOwnerWorkPayload>(
                            context.Lease.PayloadJson,
                            JsonOptions);
                if (payload is null ||
                    payload.ProcessId == Guid.Empty ||
                    payload.WorkItemId == Guid.Empty ||
                    payload.OperationRevision <= 0 ||
                    payload.Phase !=
                        TenantTerminationContributionPhase.Destroy ||
                    string.IsNullOrWhiteSpace(payload.OwnerKey))
                {
                    return false;
                }

                coordinates = new(payload.TenantId, payload.ProcessId);
                return true;
            }

            if (context.Registration.PayloadType ==
                typeof(DeleteExpiredTenantTerminationExportArtifactPayload))
            {
                DeleteExpiredTenantTerminationExportArtifactPayload? payload =
                    JsonSerializer.Deserialize<
                        DeleteExpiredTenantTerminationExportArtifactPayload>(
                            context.Lease.PayloadJson,
                            JsonOptions);
                if (payload is null ||
                    payload.ProcessId == Guid.Empty ||
                    payload.ArtifactId == Guid.Empty ||
                    payload.ExportOperationRevision <= 0 ||
                    payload.ExpiresAtUtc == default)
                {
                    return false;
                }

                coordinates = new(payload.TenantId, payload.ProcessId);
                return true;
            }

            DeleteExpiredTenantTerminationExportFragmentPayload?
                fragmentPayload = JsonSerializer.Deserialize<
                    DeleteExpiredTenantTerminationExportFragmentPayload>(
                        context.Lease.PayloadJson,
                        JsonOptions);
            if (fragmentPayload is null ||
                fragmentPayload.ProcessId == Guid.Empty ||
                fragmentPayload.FragmentId == Guid.Empty ||
                fragmentPayload.ExportOperationRevision <= 0 ||
                fragmentPayload.ExpiresAtUtc == default)
            {
                return false;
            }

            coordinates = new(
                fragmentPayload.TenantId,
                fragmentPayload.ProcessId);
            return true;
        }
        catch (Exception exception)
            when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryTenantId(string value, out string tenantId)
    {
        tenantId = string.Empty;
        if (!TenantIds.TryNormalize(value, out string? normalized) ||
            !string.Equals(value, normalized, StringComparison.Ordinal) ||
            !Guid.TryParseExact(normalized, "D", out Guid workspaceId) ||
            workspaceId == Guid.Empty ||
            !string.Equals(
                workspaceId.ToString("D"),
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        tenantId = normalized;
        return true;
    }

    private readonly record struct GlobalTenantCoordinates(
        string TenantId,
        Guid ProcessId);
}
