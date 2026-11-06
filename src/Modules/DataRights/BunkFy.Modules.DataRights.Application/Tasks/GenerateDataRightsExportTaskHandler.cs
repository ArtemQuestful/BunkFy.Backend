namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class GenerateDataRightsExportTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    IDataRightsExportArtifactGenerator generator,
    IDataRightsExportArtifactObjectStore objectStore)
    : ITaskHandler<GenerateDataRightsExportPayload>
{
    public async Task HandleAsync(
        GenerateDataRightsExportPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        DataRightsCaseScope scope = Scope(payload);
        Result<DataRightsExportGenerationStart> started =
            await commandDispatcher.DispatchAsync<
                BeginDataRightsExportGenerationCommand,
                DataRightsExportGenerationStart>(
                context,
                new BeginDataRightsExportGenerationCommand(
                    scope,
                    payload.ArtifactId,
                    payload.CaseId,
                    payload.DecisionRevision,
                    context.RunId,
                    context.Attempt),
                cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            await this.HandleFailureAsync(
                payload,
                scope,
                context,
                StartFailureCode(started.Error),
                retryable: false).ConfigureAwait(false);
            return;
        }

        if (!started.Value.DispatchRequired)
        {
            return;
        }

        DataRightsProtectedExportArtifact protectedArtifact;
        try
        {
            protectedArtifact = await generator.GenerateAsync(
                new DataRightsExportGenerationRequest(
                    started.Value.ArtifactId,
                    started.Value.TenantId,
                    started.Value.CaseId,
                    started.Value.CaseType,
                    started.Value.PropertyId,
                    started.Value.DecisionRevision,
                    started.Value.SelectedSubjects,
                    started.Value.GeneratedAtUtc,
                    started.Value.ExpiresAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = await objectStore.DeleteAsync(
                payload.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            string code = exception is DataRightsExportGenerationException generation
                ? generation.Code
                : "generation-failed";
            bool retryable = exception is not
                DataRightsExportGenerationException known ||
                known.IsRetryable;
            await this.HandleFailureAsync(
                payload,
                scope,
                context,
                code,
                retryable).ConfigureAwait(false);
            return;
        }

        Result<Unit> completed;
        try
        {
            completed = await commandDispatcher.DispatchAsync<
                    CompleteDataRightsExportGenerationCommand,
                    Unit>(
                    context,
                    new CompleteDataRightsExportGenerationCommand(
                        scope,
                        payload.ArtifactId,
                        payload.CaseId,
                        payload.DecisionRevision,
                        context.RunId,
                        context.Attempt,
                        protectedArtifact),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = await objectStore.DeleteAsync(
                payload.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await this.HandleFailureAsync(
                payload,
                scope,
                context,
                "completion-failed",
                retryable: true).ConfigureAwait(false);
            return;
        }

        if (completed.IsFailure)
        {
            await this.HandleFailureAsync(
                payload,
                scope,
                context,
                "completion-rejected",
                retryable: false).ConfigureAwait(false);
        }
    }

    private static DataRightsCaseScope Scope(
        GenerateDataRightsExportPayload payload) =>
        payload.CaseType switch
        {
            DataRightsCaseType.GuestRights when payload.PropertyId is Guid propertyId =>
                DataRightsCaseScope.ForProperty(propertyId),
            DataRightsCaseType.StaffRights when payload.PropertyId is null =>
                DataRightsCaseScope.Staff,
            _ => throw new InvalidOperationException(
                "DataRights.ExportTaskScopeInvalid")
        };

    private async Task HandleFailureAsync(
        GenerateDataRightsExportPayload payload,
        DataRightsCaseScope scope,
        TaskExecutionContext context,
        string code,
        bool retryable)
    {
        try
        {
            _ = await objectStore.DeleteAsync(
                payload.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            code = "artifact-cleanup-failed";
            retryable = true;
        }

        if (retryable && !context.IsFinalAttempt)
        {
            throw new InvalidOperationException(
                "DataRights.ExportGenerationRetryRequired");
        }

        Result<Unit> failed = await commandDispatcher.DispatchAsync<
            FailDataRightsExportGenerationCommand,
            Unit>(
            context,
            new FailDataRightsExportGenerationCommand(
                scope,
                payload.ArtifactId,
                payload.CaseId,
                payload.DecisionRevision,
                context.RunId,
                context.Attempt,
                code),
            CancellationToken.None).ConfigureAwait(false);
        if (failed.IsFailure)
        {
            throw new TaskRunTerminalFailureException(
                "data-rights.export:failure-state-rejected");
        }

        throw new TaskRunTerminalFailureException(
            $"data-rights.export:{code}");
    }

    private static string StartFailureCode(Error error) =>
        error.Code switch
        {
            "DataRights.ExportOwnerUnavailable" => "owner-unavailable",
            "DataRights.ExportOwnerCatalogInvalid" => "owner-catalog-invalid",
            _ => "generation-start-rejected"
        };
}
