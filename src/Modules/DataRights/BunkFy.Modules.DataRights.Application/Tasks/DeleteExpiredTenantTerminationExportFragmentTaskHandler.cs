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

internal sealed class DeleteExpiredTenantTerminationExportFragmentTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    ITenantTerminationExportObjectStore objectStore,
    IScopeContext scopeContext)
    : ITaskHandler<DeleteExpiredTenantTerminationExportFragmentPayload>
{
    public async Task HandleAsync(
        DeleteExpiredTenantTerminationExportFragmentPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(payload, context, scopeContext);
        Result<TenantTerminationExportObjectDeletionStart> started =
            await commandDispatcher.DispatchAsync<
                BeginTenantTerminationExportFragmentDeletionCommand,
                TenantTerminationExportObjectDeletionStart>(
                    context,
                    new(
                        payload.ProcessId,
                        payload.FragmentId,
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

        _ = await objectStore.DeleteFragmentAsync(
            payload.ProcessId,
            payload.FragmentId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Result<Unit> completed = await commandDispatcher.DispatchAsync<
            CompleteTenantTerminationExportFragmentDeletionCommand,
            Unit>(
                context,
                new(
                    payload.ProcessId,
                    payload.FragmentId,
                    payload.ExportOperationRevision,
                    context.RunId),
                cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            throw Failure(completed.Error);
        }
    }

    private static void ValidateBoundary(
        DeleteExpiredTenantTerminationExportFragmentPayload payload,
        TaskExecutionContext context,
        IScopeContext scopeContext)
    {
        Guid expectedRunId = TenantTerminationExecutionIdentity
            .CreateExportFragmentCleanupTaskRunId(payload.FragmentId);
        if (!TenantIds.TryNormalize(
                payload.TenantId,
                out string? normalizedTenantId) ||
            !string.Equals(
                payload.TenantId,
                normalizedTenantId,
                StringComparison.Ordinal) ||
            payload.ProcessId == Guid.Empty ||
            payload.FragmentId == Guid.Empty ||
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
                DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.WorkerGroup,
                DataRightsModuleMetadata.TenantTerminationWorkerGroup,
                StringComparison.Ordinal) ||
            context.PayloadVersion !=
                DeleteExpiredTenantTerminationExportFragmentPayload
                    .PayloadVersion)
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationExecutionBoundaryInvalid");
        }
    }

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
