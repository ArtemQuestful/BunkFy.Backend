namespace BunkFy.Modules.DataRights.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class DataRightsRestoreCoordinator(
    IRequestDispatcher dispatcher,
    IDataRightsLedgerDeltaStore deltaStore,
    IDataRightsReplayEnvelopeProtector replayProtector,
    IEnumerable<IDataRightsAnonymisationRestoreContributor> contributors,
    IScopeContext scopeContext,
    TimeProvider timeProvider,
    IEnumerable<IDataRightsAnonymisationRestorePrerequisiteV3>?
        restorePrerequisitesV3 = null,
    IEnumerable<IDataRightsAnonymisationRestoreContributorV3>?
        contributorsV3 = null)
    : IDataRightsRestoreCoordinator
{
    private const int PageSize = 100;

    public async Task<Result<Unit>> ReconcileAsync(
        DataRightsRestoreScope scope,
        string scopeSnapshotSha256,
        CancellationToken cancellationToken)
    {
        if (!HasValidScope(scope, scopeSnapshotSha256) ||
            !string.Equals(
                scope.ScopeId,
                scopeContext.ScopeId,
                StringComparison.Ordinal))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.RestoreEvidenceInvalid);
        }

        Result<DataRightsRestoreCheckpointState> checkpointResult =
            await dispatcher.QueryAsync(
                new GetDataRightsRestoreCheckpointQuery(),
                cancellationToken).ConfigureAwait(false);
        if (checkpointResult.IsFailure)
        {
            return Result.Failure<Unit>(checkpointResult.Error);
        }

        DataRightsRestoreCheckpointState checkpoint = checkpointResult.Value;
        DataRightsLedgerDeltaCheckpoint trusted = scope.TrustedCheckpoint;
        if (!CanReconcile(checkpoint, trusted.Cursor))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.RestoreStorageConflict);
        }

        while (checkpoint.Cursor.TenantSequence <
               trusted.Cursor.TenantSequence)
        {
            long remaining =
                trusted.Cursor.TenantSequence -
                checkpoint.Cursor.TenantSequence;
            int pageSize = (int)Math.Min(PageSize, remaining);
            DataRightsLedgerDeltaPage page = await deltaStore.ReadAfterAsync(
                scope.ScopeId,
                new DataRightsLedgerDeltaCursor(
                    checkpoint.Cursor.TenantSequence,
                    checkpoint.Cursor.EntrySha256,
                    checkpoint.StorageMacSha256),
                pageSize,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasValidPage(page, checkpoint.Cursor, trusted.Cursor, pageSize))
            {
                return Result.Failure<Unit>(
                    DataRightsApplicationErrors.RestoreEvidenceInvalid);
            }

            Result<Unit> prepared = await dispatcher.SendAsync(
                new PrepareDataRightsRestoreBatchCommand(
                    checkpoint.Version,
                    checkpoint.Cursor,
                    page.Deltas),
                cancellationToken).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return prepared;
            }

            Result<IReadOnlyList<DataRightsRestoreOwnerProofBinding>> replayed =
                await this.ReplayOwnersAsync(
                    page.Deltas,
                    cancellationToken).ConfigureAwait(false);
            if (replayed.IsFailure)
            {
                return Result.Failure<Unit>(replayed.Error);
            }

            Result<DataRightsRestoreCheckpointState> advanced =
                await dispatcher.SendAsync(
                    new AdvanceDataRightsRestoreCheckpointCommand(
                        checkpoint.Version,
                        checkpoint.Cursor,
                        page.NextCursor,
                        replayed.Value),
                    cancellationToken).ConfigureAwait(false);
            if (advanced.IsFailure)
            {
                return Result.Failure<Unit>(advanced.Error);
            }

            checkpoint = advanced.Value;
        }

        if (!MatchesTarget(checkpoint, trusted.Cursor))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.RestoreStorageConflict);
        }

        Result<DataRightsRestoreCheckpointState> confirmed =
            await dispatcher.SendAsync(
                new ConfirmDataRightsRestoreCheckpointCommand(
                    checkpoint.Version,
                    trusted,
                    scopeSnapshotSha256,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        return confirmed.IsFailure
            ? Result.Failure<Unit>(confirmed.Error)
            : Result.Success(Unit.Value);
    }

    private async Task<
        Result<IReadOnlyList<DataRightsRestoreOwnerProofBinding>>>
        ReplayOwnersAsync(
            IReadOnlyList<DataRightsLedgerDelta> deltas,
            CancellationToken cancellationToken)
    {
        List<DataRightsRestoreOwnerProofBinding> proofs =
            new(deltas.Count);
        foreach (DataRightsLedgerDelta delta in deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<DataRightsProcessingLedgerEntry> restored =
                DataRightsProcessingLedgerEntry.Restore(delta.Ledger);
            if (restored.IsFailure)
            {
                return Result.Failure<
                    IReadOnlyList<DataRightsRestoreOwnerProofBinding>>(
                        DataRightsApplicationErrors.RestoreEvidenceInvalid);
            }

            DataRightsProcessingLedgerEntry ledger = restored.Value;
            Result<DataRightsAnonymisationRestoreResult> ownerResult =
                await this.RestoreOwnerAsync(
                    delta,
                    ledger,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (ownerResult.IsFailure)
            {
                return Result.Failure<
                    IReadOnlyList<DataRightsRestoreOwnerProofBinding>>(
                        ownerResult.Error);
            }

            DataRightsAnonymisationRestoreResult result = ownerResult.Value;
            DataRightsAnonymisationRestoreProof proof = result.Proof!;
            proofs.Add(new DataRightsRestoreOwnerProofBinding(
                ledger.Id,
                ledger.TenantSequence,
                ledger.EntrySha256,
                proof.OwnerReceiptId,
                proof.OwnerReceiptSha256,
                proof.ResultingRecordVersion,
                proof.TombstoneRevision,
                proof.ReplayedAtUtc));
        }

        return Result.Success<
            IReadOnlyList<DataRightsRestoreOwnerProofBinding>>(proofs);
    }

    private Task<Result<DataRightsAnonymisationRestoreResult>>
        RestoreOwnerAsync(
            DataRightsLedgerDelta delta,
            DataRightsProcessingLedgerEntry ledger,
            CancellationToken cancellationToken) =>
        ledger.ContractVersion <=
            DataRightsProcessingLedgerEntry.GuestResultVersionContractVersion
            ? this.RestoreLegacyOwnerAsync(
                delta,
                ledger,
                cancellationToken)
            : ledger.ContractVersion ==
                DataRightsProcessingLedgerEntry.CurrentContractVersion
                ? this.RestoreScopedOwnerAsync(
                    delta,
                    ledger,
                    cancellationToken)
                : Task.FromResult(
                    Result.Failure<DataRightsAnonymisationRestoreResult>(
                        DataRightsApplicationErrors
                            .RestoreOwnerUnavailable));

    private async Task<Result<DataRightsAnonymisationRestoreResult>>
        RestoreLegacyOwnerAsync(
            DataRightsLedgerDelta delta,
            DataRightsProcessingLedgerEntry ledger,
            CancellationToken cancellationToken)
    {
        if (ledger.RoutingPropertyId is not Guid propertyId)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerUnavailable);
        }

        Result<Guid> recordId = replayProtector.Unprotect(
            delta.Ledger,
            delta.ReplayEnvelope);
        if (recordId.IsFailure)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                recordId.Error);
        }

        IDataRightsAnonymisationRestoreContributor? contributor =
            this.FindContributor(ledger.OwnerKey, ledger.RecordType);
        if (contributor is null ||
            contributor.ContractVersion !=
                DataRightsAnonymisationRestoreContract.CurrentVersion)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerUnavailable);
        }

        DataRightsAnonymisationRestoreResult result =
            await contributor.RestoreAsync(
                new DataRightsAnonymisationRestoreRequest(
                    DataRightsAnonymisationRestoreContract.CurrentVersion,
                    ledger.ScopeId,
                    ledger.Id,
                    ledger.TenantSequence,
                    ledger.EntrySha256,
                    propertyId,
                    ledger.OwnerKey,
                    ledger.RecordType,
                    recordId.Value,
                    ledger.OwnerReceiptContractVersion,
                    ledger.OwnerReceiptId,
                    ledger.OwnerReceiptSha256,
                    ledger.ResultingRecordVersion,
                    ledger.CompletedAtUtc),
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return HasMatchingProof(
                result,
                ledger,
                DataRightsAnonymisationRestoreContract.CurrentVersion)
            ? Result.Success(result)
            : Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerProofInvalid);
    }

    private async Task<Result<DataRightsAnonymisationRestoreResult>>
        RestoreScopedOwnerAsync(
            DataRightsLedgerDelta delta,
            DataRightsProcessingLedgerEntry ledger,
            CancellationToken cancellationToken)
    {
        if (ledger.CaseKind != DataRightsCaseKind.StaffRights ||
            ledger.ScopeKind != DataRightsCaseScopeKind.Tenant ||
            ledger.RoutingPropertyId.HasValue ||
            ledger.ResultingRecordVersion is not > 0)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerUnavailable);
        }

        const DataRightsCaseType caseType = DataRightsCaseType.StaffRights;
        IDataRightsAnonymisationRestorePrerequisiteV3? prerequisite =
            this.FindRestorePrerequisiteV3(
                caseType,
                ledger.OwnerKey,
                ledger.RecordType);
        if (prerequisite is null ||
            prerequisite.ContractVersion !=
                DataRightsAnonymisationRestoreContractV3.CurrentVersion)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestorePrerequisiteUnavailable);
        }

        IDataRightsAnonymisationRestoreContributorV3? contributor =
            this.FindContributorV3(
                caseType,
                ledger.OwnerKey,
                ledger.RecordType);
        if (contributor is null ||
            contributor.ContractVersion !=
                DataRightsAnonymisationRestoreContractV3.CurrentVersion)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerUnavailable);
        }

        Result<Guid> recordId = replayProtector.Unprotect(
            delta.Ledger,
            delta.ReplayEnvelope);
        if (recordId.IsFailure)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                recordId.Error);
        }

        DataRightsAnonymisationRestoreRequestV3 request = new(
            DataRightsAnonymisationRestoreContractV3.CurrentVersion,
            ledger.ScopeId,
            ledger.Id,
            ledger.TenantSequence,
            ledger.EntrySha256,
            caseType,
            DataRightsExecutionScopeKind.Tenant,
            RoutingPropertyId: null,
            ledger.OwnerKey,
            ledger.RecordType,
            recordId.Value,
            ledger.OwnerReceiptContractVersion,
            ledger.OwnerReceiptId,
            ledger.OwnerReceiptSha256,
            ledger.ResultingRecordVersion.Value,
            ledger.CompletedAtUtc);
        DataRightsAnonymisationRestorePrerequisiteResult prerequisiteResult =
            await prerequisite.ExecuteAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasValidPrerequisiteResult(prerequisiteResult))
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors
                    .RestorePrerequisiteResultInvalid);
        }

        if (prerequisiteResult.Status ==
            DataRightsAnonymisationRestorePrerequisiteStatus.Blocked)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestorePrerequisiteBlocked);
        }

        if (prerequisiteResult.Status ==
            DataRightsAnonymisationRestorePrerequisiteStatus.RetryRequired)
        {
            return Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors
                    .RestorePrerequisiteRetryRequired);
        }

        DataRightsAnonymisationRestoreResult result =
            await contributor.RestoreAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return HasMatchingProof(
                result,
                ledger,
                DataRightsAnonymisationRestoreContractV3.CurrentVersion)
            ? Result.Success(result)
            : Result.Failure<DataRightsAnonymisationRestoreResult>(
                DataRightsApplicationErrors.RestoreOwnerProofInvalid);
    }

    private IDataRightsAnonymisationRestoreContributor? FindContributor(
        string ownerKey,
        string recordType)
    {
        IDataRightsAnonymisationRestoreContributor? match = null;
        foreach (IDataRightsAnonymisationRestoreContributor contributor
                 in contributors)
        {
            if (!string.Equals(
                    contributor.OwnerKey,
                    ownerKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    contributor.RecordType,
                    recordType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = contributor;
        }

        return match;
    }

    private IDataRightsAnonymisationRestorePrerequisiteV3?
        FindRestorePrerequisiteV3(
            DataRightsCaseType caseType,
            string ownerKey,
            string recordType)
    {
        IDataRightsAnonymisationRestorePrerequisiteV3? match = null;
        foreach (IDataRightsAnonymisationRestorePrerequisiteV3 prerequisite
                 in restorePrerequisitesV3 ?? [])
        {
            if (prerequisite.CaseType != caseType ||
                !string.Equals(
                    prerequisite.OwnerKey,
                    ownerKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    prerequisite.RecordType,
                    recordType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = prerequisite;
        }

        return match;
    }

    private IDataRightsAnonymisationRestoreContributorV3? FindContributorV3(
        DataRightsCaseType caseType,
        string ownerKey,
        string recordType)
    {
        IDataRightsAnonymisationRestoreContributorV3? match = null;
        foreach (IDataRightsAnonymisationRestoreContributorV3 contributor
                 in contributorsV3 ?? [])
        {
            if (contributor.CaseType != caseType ||
                !string.Equals(
                    contributor.OwnerKey,
                    ownerKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    contributor.RecordType,
                    recordType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = contributor;
        }

        return match;
    }

    private static bool HasValidScope(
        DataRightsRestoreScope? scope,
        string scopeSnapshotSha256) =>
        scope is not null &&
        scope.ContractVersion == DataRightsRestoreScope.CurrentContractVersion &&
        !string.IsNullOrWhiteSpace(scope.ScopeId) &&
        scope.TrustedCheckpoint is not null &&
        scope.TrustedCheckpoint.ContractVersion ==
            DataRightsLedgerDeltaCheckpoint.CurrentContractVersion &&
        scope.TrustedCheckpoint.Cursor is not null &&
        scope.TrustedCheckpoint.Cursor.TenantSequence >= 0 &&
        IsSha256(scope.TrustedCheckpoint.Cursor.EntrySha256) &&
        IsSha256(scope.TrustedCheckpoint.Cursor.StorageMacSha256) &&
        scope.TrustedCheckpoint.IntegrityKeyVersion > 0 &&
        IsSha256(scope.TrustedCheckpoint.CheckpointMacSha256) &&
        IsSha256(scopeSnapshotSha256);

    private static bool CanReconcile(
        DataRightsRestoreCheckpointState checkpoint,
        DataRightsLedgerDeltaCursor target) =>
        checkpoint.Cursor.HasValidShape() &&
        IsSha256(checkpoint.StorageMacSha256) &&
        checkpoint.Cursor.TenantSequence <= target.TenantSequence &&
        (checkpoint.Cursor.TenantSequence != target.TenantSequence ||
         MatchesTarget(checkpoint, target));

    private static bool MatchesTarget(
        DataRightsRestoreCheckpointState checkpoint,
        DataRightsLedgerDeltaCursor target) =>
        checkpoint.Cursor.TenantSequence == target.TenantSequence &&
        string.Equals(
            checkpoint.Cursor.EntrySha256,
            target.EntrySha256,
            StringComparison.Ordinal) &&
        string.Equals(
            checkpoint.StorageMacSha256,
            target.StorageMacSha256,
            StringComparison.Ordinal);

    private static bool HasValidPage(
        DataRightsLedgerDeltaPage page,
        DataRightsRestoreCursor current,
        DataRightsLedgerDeltaCursor target,
        int requestedPageSize)
    {
        if (page is null ||
            page.ContractVersion !=
                DataRightsLedgerDeltaPage.CurrentContractVersion ||
            page.Deltas is null ||
            page.Deltas.Count is < 1 ||
            page.Deltas.Count > requestedPageSize ||
            page.NextCursor is null ||
            page.NextCursor.TenantSequence >
                target.TenantSequence)
        {
            return false;
        }

        DataRightsProcessingLedgerSnapshot last =
            page.Deltas[^1].Ledger;
        return page.NextCursor.TenantSequence == last.TenantSequence &&
            string.Equals(
                page.NextCursor.EntrySha256,
                last.EntrySha256,
                StringComparison.Ordinal) &&
            IsSha256(page.NextCursor.StorageMacSha256) &&
            last.TenantSequence > current.TenantSequence;
    }

    private static bool HasMatchingProof(
        DataRightsAnonymisationRestoreResult? result,
        DataRightsProcessingLedgerEntry ledger,
        int expectedContractVersion)
    {
        DataRightsAnonymisationRestoreProof? proof = result?.Proof;
        return result?.ContractVersion == expectedContractVersion &&
            result.Status == DataRightsAnonymisationRestoreStatus.Completed &&
            proof is not null &&
            proof.LedgerEntryId == ledger.Id &&
            proof.OwnerReceiptId == ledger.OwnerReceiptId &&
            string.Equals(
                proof.OwnerReceiptSha256,
                ledger.OwnerReceiptSha256,
                StringComparison.Ordinal) &&
            HasMatchingResultVersion(proof, ledger) &&
            proof.TombstoneRevision > 0 &&
            proof.ReplayedAtUtc != default &&
            proof.ReplayedAtUtc.Offset == TimeSpan.Zero;
    }

    private static bool HasMatchingResultVersion(
        DataRightsAnonymisationRestoreProof proof,
        DataRightsProcessingLedgerEntry ledger) =>
        ledger.ContractVersion >= 2
            ? ledger.ResultingRecordVersion.HasValue &&
              proof.ResultingRecordVersion ==
                  ledger.ResultingRecordVersion.Value
            : proof.ResultingRecordVersion > 0;

    private static bool HasValidPrerequisiteResult(
        DataRightsAnonymisationRestorePrerequisiteResult? result)
    {
        if (result?.ContractVersion !=
            DataRightsAnonymisationRestoreContractV3.CurrentVersion)
        {
            return false;
        }

        return result.Status switch
        {
            DataRightsAnonymisationRestorePrerequisiteStatus.Completed =>
                result.OutcomeCode is null,
            DataRightsAnonymisationRestorePrerequisiteStatus.Blocked or
                DataRightsAnonymisationRestorePrerequisiteStatus
                    .RetryRequired =>
                IsCode(
                    result.OutcomeCode,
                    DataRightsAnonymisationContract.CodeMaxLength),
            _ => false
        };
    }

    private static bool IsCode(string? value, int maxLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
            normalized.Length <= maxLength &&
            string.Equals(value, normalized, StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: DataRightsProcessingLedgerEntry.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
