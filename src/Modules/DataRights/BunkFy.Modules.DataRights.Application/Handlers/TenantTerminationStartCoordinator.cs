namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class TenantTerminationStartCoordinator(
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationRepository processes,
    ITenantTerminationReplayStore replayStore,
    ITenantTerminationProductionCatalog productionCatalog,
    ITenantTerminationRequiredOwnerCatalog requiredOwners,
    ITenantTerminationCoordinationSignal coordinationSignal,
    IScopeContext scopeContext,
    ISystemClock clock)
{
    public Task<Result<TenantTerminationStartDto>> StartAsync(
        StartTenantTerminationCommand command,
        CancellationToken cancellationToken) =>
        this.ExecuteAsync(
            command,
            requireProtectedIntent: false,
            requiredProcessPresence: null,
            cancellationToken);

    public Task<Result<TenantTerminationStartDto>> RecoverAsync(
        StartTenantTerminationCommand command,
        bool requireExistingProcess,
        CancellationToken cancellationToken) =>
        this.ExecuteAsync(
            command,
            requireProtectedIntent: true,
            requiredProcessPresence: requireExistingProcess,
            cancellationToken);

    private async Task<Result<TenantTerminationStartDto>> ExecuteAsync(
        StartTenantTerminationCommand command,
        bool requireProtectedIntent,
        bool? requiredProcessPresence,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors.TenantRequired);
        }

        Result<TenantTerminationApprovalEvidence> normalizedEvidence =
            NormalizeEvidence(command.ApprovalEvidence);
        if (normalizedEvidence.IsFailure)
        {
            return Result.Failure<TenantTerminationStartDto>(
                normalizedEvidence.Error);
        }

        TenantTerminationMutationState? state =
            await mutations.AcquireAdmissionAsync(
                command.ProcessId,
                command.CaseId,
                cancellationToken).ConfigureAwait(false);
        DataRightsCase? dataRightsCase = state?.Case;
        if (dataRightsCase is null)
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors.TenantTerminationCaseNotFound);
        }

        string policyEvidenceSha256 =
            normalizedEvidence.Value.ComputeSha256();
        if (!TenantTerminationApprovalEvidenceContract
                .FixedTimeSha256Equals(
                    dataRightsCase
                        .TenantTerminationPolicyEvidenceSha256,
                    policyEvidenceSha256))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationApprovalEvidenceInvalid);
        }

        Guid idempotencyKey = TenantTerminationExecutionIdentity
            .CreateProcessIdempotencyKey(command.ProcessId);
        Guid terminationEpoch = TenantTerminationExecutionIdentity
            .CreateTerminationEpoch(command.ProcessId);
        TenantTerminationReplayIntent? existingIntent;
        try
        {
            existingIntent = await replayStore.ReadIntentAsync(
                scopeContext.ScopeId,
                command.ProcessId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TenantTerminationReplayStoreException)
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationReplayIntentInvalid);
        }

        TenantTerminationProcess? existingProcess = state!.Process;
        string recoveryActor = command.ActorId?.Trim() ?? string.Empty;
        if (!IsActor(recoveryActor))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors.TransitionInvalid);
        }

        string executionActor = requireProtectedIntent && existingIntent is not null
            ? existingIntent.ExecutingActorId
            : recoveryActor;
        if (existingProcess is not null)
        {
            if (requiredProcessPresence is false ||
                existingProcess.CaseId != command.CaseId)
            {
                return Result.Failure<TenantTerminationStartDto>(
                    DataRightsApplicationErrors
                        .TenantTerminationStartConflict);
            }

            return MatchesExistingExecution(
                    dataRightsCase,
                    existingProcess,
                    existingIntent,
                    normalizedEvidence.Value,
                    idempotencyKey,
                    terminationEpoch,
                    executionActor)
                    ? Success(dataRightsCase, existingProcess)
                    : Result.Failure<TenantTerminationStartDto>(
                        DataRightsApplicationErrors
                            .TenantTerminationStartConflict);
        }

        if (requiredProcessPresence is true)
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationStartConflict);
        }

        Result startable = ValidateStartableCase(
            dataRightsCase,
            command.ExpectedCaseVersion,
            executionActor,
            clock.UtcNow);
        if (startable.IsFailure)
        {
            return Result.Failure<TenantTerminationStartDto>(
                startable.Error);
        }

        if (await processes.GetActiveProcessAsync(cancellationToken)
                .ConfigureAwait(false) is not null ||
            await processes.GetProcessByIdempotencyKeyAsync(
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationStartConflict);
        }

        Result<TenantTerminationProductionCatalogEvidence> catalog =
            productionCatalog.Validate(requiredOwners.RequiredOwnerKeys);
        if (catalog.IsFailure)
        {
            return Result.Failure<TenantTerminationStartDto>(catalog.Error);
        }

        if (!TenantTerminationApprovalEvidenceContract.FixedTimeSha256Equals(
                normalizedEvidence.Value.OwnerCatalogSha256,
                catalog.Value.CatalogSha256))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationApprovalEvidenceInvalid);
        }

        if (requireProtectedIntent && existingIntent is null)
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationReplayIntentInvalid);
        }

        TenantTerminationReplayIntent intent = existingIntent ??
            CreateIntent(
                dataRightsCase,
                scopeContext.ScopeId,
                command.ProcessId,
                normalizedEvidence.Value.OwnerCatalogSha256,
                idempotencyKey,
                terminationEpoch,
                executionActor,
                clock.UtcNow);
        if (!MatchesIntent(
                intent,
                dataRightsCase,
                normalizedEvidence.Value.OwnerCatalogSha256,
                idempotencyKey,
                terminationEpoch,
                executionActor,
                requireCaseExecution: false))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationReplayIntentInvalid);
        }

        if (existingIntent is null)
        {
            Result protectedIntent = await this.ProtectIntentAsync(
                intent,
                cancellationToken).ConfigureAwait(false);
            if (protectedIntent.IsFailure)
            {
                return Result.Failure<TenantTerminationStartDto>(
                    protectedIntent.Error);
            }
        }

        Result began = dataRightsCase.BeginTenantTerminationExecution(
            command.ExpectedCaseVersion,
            executionActor,
            intent.ExecutionStartedAtUtc);
        if (began.IsFailure)
        {
            return Result.Failure<TenantTerminationStartDto>(began.Error);
        }

        Result<TenantTerminationProcess> prepared =
            TenantTerminationProcess.Prepare(
                command.ProcessId,
                scopeContext.ScopeId,
                idempotencyKey,
                dataRightsCase.Id,
                intent.ApprovalRevision,
                terminationEpoch,
                intent.ExportRequested,
                intent.PolicyEvidenceSha256,
                intent.ApprovedBy,
                intent.ApprovedAtUtc,
                executionActor,
                intent.ExecutionStartedAtUtc);
        if (prepared.IsFailure)
        {
            return Result.Failure<TenantTerminationStartDto>(
                prepared.Error);
        }

        await processes.AddProcessAsync(
            prepared.Value,
            cancellationToken).ConfigureAwait(false);
        if (!await coordinationSignal.EnqueueAsync(
                prepared.Value,
                intent.ExecutionStartedAtUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<TenantTerminationStartDto>(
                DataRightsApplicationErrors
                    .TenantTerminationExecutionStateInvalid);
        }

        return Success(dataRightsCase, prepared.Value);
    }

    private async Task<Result> ProtectIntentAsync(
        TenantTerminationReplayIntent intent,
        CancellationToken cancellationToken)
    {
        TenantTerminationReplayJournalEntry entry =
            TenantTerminationReplayJournalEntry.ForIntent(intent);
        try
        {
            TenantTerminationReplayAppendReceipt receipt =
                await replayStore.AppendAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return TenantTerminationReplayDurability.Matches(receipt, entry)
                    ? Result.Success()
                    : Result.Failure(
                        DataRightsApplicationErrors
                            .TenantTerminationReplayIntentInvalid);
        }
        catch (TenantTerminationReplayStoreException)
        {
            return Result.Failure(
                DataRightsApplicationErrors
                    .TenantTerminationReplayIntentInvalid);
        }
    }

    private static Result<TenantTerminationApprovalEvidence>
        NormalizeEvidence(TenantTerminationApprovalEvidence? evidence)
    {
        if (evidence is null ||
            !TenantTerminationApprovalEvidenceContract.TryCreate(
                evidence.ApprovalReference,
                evidence.OwnerCatalogSha256,
                evidence.BackupEvidenceReference,
                evidence.RestoreDrillEvidenceReference,
                evidence.OperatorAssuranceReference,
                out TenantTerminationApprovalEvidence? normalized))
        {
            return Result.Failure<TenantTerminationApprovalEvidence>(
                DataRightsApplicationErrors
                    .TenantTerminationApprovalEvidenceInvalid);
        }

        return Result.Success(normalized!);
    }

    private static Result ValidateStartableCase(
        DataRightsCase dataRightsCase,
        long expectedVersion,
        string actor,
        DateTimeOffset nowUtc)
    {
        if (dataRightsCase.Version != expectedVersion)
        {
            return Result.Failure(
                DataRightsApplicationErrors.VersionConflict);
        }

        return dataRightsCase.Status == DataRightsCaseState.Approved &&
            dataRightsCase.Decision == DataRightsCaseDecision.Approved &&
            dataRightsCase.DecisionRevision is > 0 &&
            dataRightsCase.DecidedAtUtc.HasValue &&
            dataRightsCase.TenantTerminationExportRequested.HasValue &&
            actor.Length is > 0 and <= DataRightsCase.ActorIdMaxLength &&
            !string.Equals(
                actor,
                dataRightsCase.DecidedBy,
                StringComparison.Ordinal) &&
            nowUtc != default &&
            nowUtc >= dataRightsCase.LastChangedAtUtc
                ? Result.Success()
                : Result.Failure(
                    DataRightsApplicationErrors.TransitionInvalid);
    }

    private static bool IsActor(string actor) =>
        actor.Length is > 0 and <= DataRightsCase.ActorIdMaxLength;

    private static TenantTerminationReplayIntent CreateIntent(
        DataRightsCase dataRightsCase,
        string tenantId,
        Guid processId,
        string ownerCatalogSha256,
        Guid idempotencyKey,
        Guid terminationEpoch,
        string actor,
        DateTimeOffset nowUtc) =>
        TenantTerminationReplayIntent.Create(
            tenantId,
            processId,
            dataRightsCase.Id,
            (DataRightsRequesterRelationship)
                dataRightsCase.RequesterRelationship,
            dataRightsCase.CreatedBy,
            dataRightsCase.CreatedAtUtc,
            dataRightsCase.TenantTerminationExportRequested!.Value,
            dataRightsCase.DecisionRevision!.Value,
            dataRightsCase.DecidedBy!,
            dataRightsCase.DecidedAtUtc!.Value,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256!,
            ownerCatalogSha256,
            idempotencyKey,
            terminationEpoch,
            actor,
            nowUtc,
            nowUtc);

    private static bool MatchesExistingExecution(
        DataRightsCase dataRightsCase,
        TenantTerminationProcess process,
        TenantTerminationReplayIntent? intent,
        TenantTerminationApprovalEvidence evidence,
        Guid idempotencyKey,
        Guid terminationEpoch,
        string actor) =>
        intent is not null &&
        MatchesIntent(
            intent,
            dataRightsCase,
            evidence.OwnerCatalogSha256,
            idempotencyKey,
            terminationEpoch,
            actor,
            requireCaseExecution: true) &&
        process.Matches(
            idempotencyKey,
            dataRightsCase.Id,
            intent.ApprovalRevision) &&
        process.TerminationEpoch == terminationEpoch &&
        process.ExportRequested == intent.ExportRequested &&
        TenantTerminationApprovalEvidenceContract.FixedTimeSha256Equals(
            process.PolicyEvidenceSha256,
            intent.PolicyEvidenceSha256) &&
        string.Equals(
            process.ApprovedBy,
            intent.ApprovedBy,
            StringComparison.Ordinal) &&
        process.ApprovedAtUtc == intent.ApprovedAtUtc &&
        string.Equals(
            process.CreatedBy,
            actor,
            StringComparison.Ordinal) &&
        process.CreatedAtUtc == intent.ExecutionStartedAtUtc;

    private static bool MatchesIntent(
        TenantTerminationReplayIntent intent,
        DataRightsCase dataRightsCase,
        string ownerCatalogSha256,
        Guid idempotencyKey,
        Guid terminationEpoch,
        string actor,
        bool requireCaseExecution) =>
        intent.HasValidProof() &&
        string.Equals(
            intent.TenantId,
            dataRightsCase.ScopeId,
            StringComparison.Ordinal) &&
        intent.CaseId == dataRightsCase.Id &&
        intent.RequesterRelationship ==
            (DataRightsRequesterRelationship)
                dataRightsCase.RequesterRelationship &&
        string.Equals(
            intent.RequestedBy,
            dataRightsCase.CreatedBy,
            StringComparison.Ordinal) &&
        intent.RequestedAtUtc == dataRightsCase.CreatedAtUtc &&
        intent.ExportRequested ==
            dataRightsCase.TenantTerminationExportRequested &&
        intent.ApprovalRevision == dataRightsCase.DecisionRevision &&
        string.Equals(
            intent.ApprovedBy,
            dataRightsCase.DecidedBy,
            StringComparison.Ordinal) &&
        intent.ApprovedAtUtc == dataRightsCase.DecidedAtUtc &&
        TenantTerminationApprovalEvidenceContract.FixedTimeSha256Equals(
            intent.PolicyEvidenceSha256,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256) &&
        TenantTerminationApprovalEvidenceContract.FixedTimeSha256Equals(
            intent.ApprovedOwnerCatalogSha256,
            ownerCatalogSha256) &&
        intent.IdempotencyKey == idempotencyKey &&
        intent.TerminationEpoch == terminationEpoch &&
        string.Equals(
            intent.ExecutingActorId,
            actor,
            StringComparison.Ordinal) &&
        (!requireCaseExecution ||
            ((dataRightsCase.Status is
                    DataRightsCaseState.Executing or
                    DataRightsCaseState.Completed or
                    DataRightsCaseState.Canceled) &&
                dataRightsCase.ExecutionStartedAtUtc ==
                    intent.ExecutionStartedAtUtc));

    private static Result<TenantTerminationStartDto> Success(
        DataRightsCase dataRightsCase,
        TenantTerminationProcess process) =>
        Result.Success(new TenantTerminationStartDto(
            dataRightsCase.ToTenantTerminationDto(),
            process.ToDto()));
}
