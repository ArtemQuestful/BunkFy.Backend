namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class PrepareTenantTerminationExportDownloadCommandHandler(
    ITenantTerminationRepository processes,
    ITenantTerminationExportArtifactRepository artifacts,
    ITenantTerminationExportArtifactReader reader,
    IDataRightsExportAuditSink audit,
    IScopeContext scopeContext,
    ISystemClock clock)
    : ICommandHandler<PrepareTenantTerminationExportDownloadCommand,
        DataRightsExportDownload>
{
    public async Task<Result<DataRightsExportDownload>> HandleAsync(
        PrepareTenantTerminationExportDownloadCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsExportDownload>(
                DataRightsApplicationErrors.TenantRequired);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        TenantTerminationProcess? process = await processes.GetProcessAsync(
            command.ProcessId,
            cancellationToken).ConfigureAwait(false);
        TenantTerminationExportArtifact? artifact = await artifacts.GetAsync(
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (process is null ||
            artifact is null ||
            process.CaseId != command.CaseId ||
            artifact.CaseId != command.CaseId ||
            !string.Equals(
                process.ScopeId,
                scopeContext.ScopeId,
                StringComparison.Ordinal) ||
            !TenantTerminationExportArtifactCoordinator.IsAvailableForDownload(
                process,
                artifact,
                nowUtc))
        {
            Error error = artifact is not null && nowUtc >= artifact.ExpiresAtUtc
                ? DataRightsApplicationErrors
                    .TenantTerminationExportArtifactExpired
                : artifact is null
                    ? DataRightsApplicationErrors
                        .TenantTerminationExportArtifactNotFound
                    : DataRightsApplicationErrors
                        .TenantTerminationExportArtifactNotAvailable;
            await this.RecordAsync(
                command.ArtifactId,
                command.CaseId,
                command.ActorId,
                error.Code,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            return Result.Failure<DataRightsExportDownload>(error);
        }

        DataRightsExportDownload download;
        try
        {
            download = await reader.OpenVerifiedAsync(
                artifact,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await this.RecordAsync(
                artifact,
                command.ActorId,
                DataRightsApplicationErrors
                    .TenantTerminationExportVerificationFailed.Code,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            return Result.Failure<DataRightsExportDownload>(
                DataRightsApplicationErrors
                    .TenantTerminationExportVerificationFailed);
        }

        DateTimeOffset verifiedAtUtc = clock.UtcNow;
        if (verifiedAtUtc >= artifact.ExpiresAtUtc)
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            await this.RecordAsync(
                artifact,
                command.ActorId,
                DataRightsApplicationErrors
                    .TenantTerminationExportArtifactExpired.Code,
                verifiedAtUtc,
                cancellationToken).ConfigureAwait(false);
            return Result.Failure<DataRightsExportDownload>(
                DataRightsApplicationErrors
                    .TenantTerminationExportArtifactExpired);
        }

        try
        {
            await this.RecordAsync(
                artifact,
                command.ActorId,
                "succeeded",
                verifiedAtUtc,
                cancellationToken).ConfigureAwait(false);
            return Result.Success(download);
        }
        catch
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task RecordAsync(
        TenantTerminationExportArtifact artifact,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        audit.RecordAsync(
            DataRightsExportAuditFacts.Create(
                artifact,
                DataRightsExportAuditAction.Download,
                actorId,
                outcomeCode,
                occurredAtUtc),
            cancellationToken);

    private Task RecordAsync(
        Guid artifactId,
        Guid caseId,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new DataRightsExportAuditFact(
                scopeContext.ScopeId!,
                artifactId,
                caseId,
                DataRightsCaseType.TenantTermination,
                PropertyId: null,
                DataRightsExportAuditAction.Download,
                actorId,
                outcomeCode,
                occurredAtUtc),
            cancellationToken);
}
