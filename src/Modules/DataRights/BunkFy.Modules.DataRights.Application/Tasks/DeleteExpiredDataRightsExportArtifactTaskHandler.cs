namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class DeleteExpiredDataRightsExportArtifactTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    IDataRightsExportArtifactObjectStore objectStore)
    : ITaskHandler<DeleteExpiredDataRightsExportArtifactPayload>
{
    public async Task HandleAsync(
        DeleteExpiredDataRightsExportArtifactPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        DataRightsCaseScope scope = Scope(payload);
        Result<DataRightsExportDeletionStart> started =
            await commandDispatcher.DispatchAsync<
                BeginDataRightsExportDeletionCommand,
                DataRightsExportDeletionStart>(
                context,
                new BeginDataRightsExportDeletionCommand(
                    scope,
                    payload.ArtifactId,
                    payload.CaseId,
                    payload.DecisionRevision,
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

        _ = await objectStore.DeleteAsync(
            started.Value.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Result<Unit> completed =
            await commandDispatcher.DispatchAsync<
                CompleteDataRightsExportDeletionCommand,
                Unit>(
                context,
                new CompleteDataRightsExportDeletionCommand(
                    scope,
                    payload.ArtifactId,
                    payload.CaseId,
                    payload.DecisionRevision,
                    context.RunId),
                cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            throw Failure(completed.Error);
        }
    }

    private static DataRightsCaseScope Scope(
        DeleteExpiredDataRightsExportArtifactPayload payload) =>
        payload.CaseType switch
        {
            DataRightsCaseType.GuestRights when payload.PropertyId is Guid propertyId =>
                DataRightsCaseScope.ForProperty(propertyId),
            DataRightsCaseType.StaffRights when payload.PropertyId is null =>
                DataRightsCaseScope.Staff,
            _ => throw new InvalidOperationException(
                "DataRights.ExportCleanupTaskScopeInvalid")
        };

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
