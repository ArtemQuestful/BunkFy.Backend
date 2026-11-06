namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Naming;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class DeleteExpiredTenantTerminationExportArtifactTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    ITenantTerminationExportObjectStore objectStore,
    IScopeContext scopeContext)
    : ITaskHandler<DeleteExpiredTenantTerminationExportArtifactPayload>
{
    public async Task HandleAsync(
        DeleteExpiredTenantTerminationExportArtifactPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(payload, context, scopeContext);
        Result<TenantTerminationExportObjectDeletionStart> started =
            await commandDispatcher.DispatchAsync<
                BeginTenantTerminationExportArtifactDeletionCommand,
                TenantTerminationExportObjectDeletionStart>(
                    context,
                    new(
                        payload.ProcessId,
                        payload.ArtifactId,
                        payload.ExportOperationRevision,
                        payload.ExpiresAtUtc,
                        context.RunId),
                    cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (!started.Value.DeletionRequired)
        {
            return;
        }

        _ = await objectStore.DeleteArtifactAsync(
            payload.ProcessId,
            payload.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Result<Unit> completed = await commandDispatcher.DispatchAsync<
            CompleteTenantTerminationExportArtifactDeletionCommand,
            Unit>(
                context,
                new(
                    payload.ProcessId,
                    payload.ArtifactId,
                    payload.ExportOperationRevision,
                    context.RunId),
                cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            throw Failure(completed.Error);
        }
    }

    private static void ValidateBoundary(
        DeleteExpiredTenantTerminationExportArtifactPayload payload,
        TaskExecutionContext context,
        IScopeContext scopeContext)
    {
        Guid expectedRunId = TenantTerminationExecutionIdentity
            .CreateExportArtifactCleanupTaskRunId(payload.ArtifactId);
        if (!TenantIds.TryNormalize(
                payload.TenantId,
                out string? normalizedTenantId) ||
            !string.Equals(
                payload.TenantId,
                normalizedTenantId,
                StringComparison.Ordinal) ||
            payload.ProcessId == Guid.Empty ||
            payload.ArtifactId == Guid.Empty ||
            payload.ExportOperationRevision <= 0 ||
            payload.ExpiresAtUtc == default ||
            context.RunId != expectedRunId ||
            context.CorrelationId != payload.ProcessId ||
            !scopeContext.IsEnabled ||
            context.ScopeId is not null ||
            !string.Equals(
                scopeContext.ScopeId,
                normalizedTenantId,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.ModuleName,
                DataRightsModuleMetadata.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.TaskName,
                DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.WorkerGroup,
                DataRightsModuleMetadata.TenantTerminationWorkerGroup,
                StringComparison.Ordinal) ||
            context.PayloadVersion !=
                DeleteExpiredTenantTerminationExportArtifactPayload
                    .PayloadVersion)
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }
    }

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
