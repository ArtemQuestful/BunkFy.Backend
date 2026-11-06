namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class RetryDataRightsExportCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IDataRightsExportArtifactRepository artifacts,
    IEnumerable<IDataRightsSubjectExportContributor> contributors,
    IDataRightsExportAuditSink audit,
    IOutboxWriterRegistry outboxWriters,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<RetryDataRightsExportCommand, DataRightsExportArtifactDto>
{
    public async Task<Result<DataRightsExportArtifactDto>> HandleAsync(
        RetryDataRightsExportCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.TenantRequired);
        }

        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        DataRightsExportArtifact? artifact = await artifacts.GetAsync(
            command.Scope,
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null || artifact is null ||
            artifact.CaseId != command.CaseId)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportArtifactNotFound);
        }

        DataRightsSubjectCoordinate[] subjects =
            RequestDataRightsExportCommandHandler.ToCoordinates(dataRightsCase);
        if (!artifact.MatchesCaseSnapshot(
                command.CaseId,
                dataRightsCase.DecisionRevision ?? 0,
                DataRightsExportIdentity.SelectionSha256(subjects)))
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportGenerationConflict);
        }

        if (artifact.IsRetryReplay(command.ExpectedArtifactVersion))
        {
            return Result.Success(artifact.ToDto());
        }

        if (!DataRightsExportGenerationCase.IsEligible(
                dataRightsCase,
                command.Scope,
                command.ExpectedCaseVersion,
                subjects))
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportNotEligible);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (nowUtc >= artifact.ExpiresAtUtc)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportArtifactExpired);
        }

        Result<IReadOnlyDictionary<string, IDataRightsSubjectExportContributor>>
            contributorSet = DataRightsExportContributorSet.Resolve(
                contributors,
                command.Scope.CaseType,
                subjects);
        if (contributorSet.IsFailure)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                contributorSet.Error);
        }

        Result transition = artifact.RequestRetry(
            command.ExpectedArtifactVersion,
            nowUtc);
        if (transition.IsFailure)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                transition.Error == DataRightsDomainErrors.VersionConflict
                    ? DataRightsApplicationErrors.ExportArtifactVersionConflict
                    : DataRightsApplicationErrors.ExportArtifactTransitionInvalid);
        }

        await outboxWriters.GetRequired(DataRightsModuleMetadata.Name).EnqueueAsync(
            new DataRightsExportArtifactRequestedIntegrationEvent(
                ids.NewId(),
                scopeContext.ScopeId,
                nowUtc,
                artifact.Id,
                artifact.CaseId,
                command.Scope.CaseType,
                artifact.PropertyId,
                artifact.DecisionRevision,
                artifact.ExpiresAtUtc),
            cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync(
            DataRightsExportAuditFacts.Create(
                artifact,
                DataRightsExportAuditAction.GenerationRequested,
                command.ActorId,
                "retry-requested",
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(artifact.ToDto());
    }
}
