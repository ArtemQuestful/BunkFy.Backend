namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class RequestDataRightsExportCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IDataRightsExportArtifactRepository artifacts,
    IEnumerable<IDataRightsSubjectExportContributor> contributors,
    IDataRightsExportArtifactPolicy exportPolicy,
    IDataRightsExportAuditSink audit,
    IOutboxWriterRegistry outboxWriters,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<RequestDataRightsExportCommand, DataRightsExportArtifactDto>
{
    public async Task<Result<DataRightsExportArtifactDto>> HandleAsync(
        RequestDataRightsExportCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.TenantRequired);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        DataRightsSubjectCoordinate[] subjects = ToCoordinates(dataRightsCase);
        string selectionSha256 = DataRightsExportIdentity.SelectionSha256(subjects);
        DataRightsExportArtifact? byCase = await artifacts.GetByCaseAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (byCase is not null)
        {
            long decisionRevision = dataRightsCase.DecisionRevision ?? 0;
            if (!byCase.MatchesCaseSnapshot(
                    command.CaseId,
                    decisionRevision,
                    selectionSha256))
            {
                return Result.Failure<DataRightsExportArtifactDto>(
                    DataRightsApplicationErrors.ExportArtifactAlreadyRequested);
            }

            if (!byCase.Matches(
                command.IdempotencyKey,
                command.CaseId,
                decisionRevision,
                selectionSha256))
            {
                return Result.Failure<DataRightsExportArtifactDto>(
                    DataRightsApplicationErrors.ExportArtifactAlreadyRequested);
            }

            return Result.Success(byCase.ToDto());
        }

        DataRightsExportArtifact? byKey = await artifacts.GetByIdempotencyKeyAsync(
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (byKey is not null)
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportArtifactAlreadyRequested);
        }

        if (!DataRightsExportGenerationCase.IsEligible(
                dataRightsCase,
                command.Scope,
                command.ExpectedVersion,
                subjects))
        {
            return Result.Failure<DataRightsExportArtifactDto>(
                DataRightsApplicationErrors.ExportNotEligible);
        }

        Result<IReadOnlyDictionary<string, IDataRightsSubjectExportContributor>>
            contributorSet = DataRightsExportContributorSet.Resolve(
                contributors,
                command.Scope.CaseType,
                subjects);
        if (contributorSet.IsFailure)
        {
            return Result.Failure<DataRightsExportArtifactDto>(contributorSet.Error);
        }

        DateTimeOffset expiresAtUtc = exportPolicy.ExpiresAt(nowUtc);
        Result<DataRightsExportArtifact> requested =
            DataRightsExportArtifact.Request(
                ids.NewId(),
                scopeContext.ScopeId!,
                command.IdempotencyKey,
                dataRightsCase.Id,
                dataRightsCase.PropertyId,
                dataRightsCase.Kind,
                dataRightsCase.DecisionRevision!.Value,
                subjects.Length,
                selectionSha256,
                command.ActorId,
                nowUtc,
                expiresAtUtc);
        if (requested.IsFailure)
        {
            return Result.Failure<DataRightsExportArtifactDto>(requested.Error);
        }

        await artifacts.AddAsync(requested.Value, cancellationToken)
            .ConfigureAwait(false);
        await this.EnqueueRequestedAsync(
            requested.Value,
            command.Scope.CaseType,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync(
            DataRightsExportAuditFacts.Create(
                requested.Value,
                DataRightsExportAuditAction.GenerationRequested,
                command.ActorId,
                "requested",
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(requested.Value.ToDto());
    }

    private async Task EnqueueRequestedAsync(
        DataRightsExportArtifact artifact,
        DataRightsCaseType caseType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await outboxWriters.GetRequired(DataRightsModuleMetadata.Name).EnqueueAsync(
            new DataRightsExportArtifactRequestedIntegrationEvent(
                ids.NewId(),
                scopeContext.ScopeId!,
                nowUtc,
                artifact.Id,
                artifact.CaseId,
                caseType,
                artifact.PropertyId,
                artifact.DecisionRevision,
                artifact.ExpiresAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    internal static DataRightsSubjectCoordinate[] ToCoordinates(
        DataRightsCase dataRightsCase) =>
        dataRightsCase.SelectedSubjects
            .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordType, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordId)
            .Select(subject => new DataRightsSubjectCoordinate(
                subject.OwnerKey,
                subject.RecordType,
                subject.RecordId,
                subject.RecordVersion))
            .ToArray();
}
