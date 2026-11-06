namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class FinalizeDataRightsAnonymisationLedgerCommandHandler(
    DataRightsExecutionMutationCoordinator mutations,
    IDataRightsExecutionWorkItemRepository workItems,
    IDataRightsProcessingLedgerRepository ledgers,
    IDataRightsRecordPseudonymizer pseudonymizer,
    IDataRightsReplayEnvelopeProtector replayProtector,
    IDataRightsLedgerDeltaStore deltaStore,
    IOutboxWriterRegistry outboxWriters,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<FinalizeDataRightsAnonymisationLedgerCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        FinalizeDataRightsAnonymisationLedgerCommand command,
        CancellationToken cancellationToken)
    {
        DataRightsCase? dataRightsCase =
            await mutations.AcquireLedgerWorkItemAsync(
            command.Scope,
            command.CaseId,
            command.WorkItemId,
            cancellationToken).ConfigureAwait(false);
        DataRightsExecutionWorkItem? workItem = await workItems.GetAsync(
            command.Scope,
            command.CaseId,
            command.WorkItemId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null || workItem is null)
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ExecutionNotFound);
        }

        DataRightsExecutionScope executionScope =
            command.Scope.ToExecutionScope();
        if (!workItem.HasExecutionCoordinates(
                command.CaseId,
                executionScope,
                command.ApprovalRevision,
                command.ExecutionRevision) ||
            workItem.Operation != DataRightsCaseOperation.Anonymisation ||
            dataRightsCase.ExecutionRevision != command.ExecutionRevision ||
            workItem.TaskRunId != command.TaskRunId)
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ExecutionCoordinateInvalid);
        }

        if (workItem.State is DataRightsExecutionWorkItemState.Blocked
                or DataRightsExecutionWorkItemState.Failed
                or DataRightsExecutionWorkItemState.NoOp)
        {
            return Result.Success(Unit.Value);
        }

        if (workItem.State is not (
                DataRightsExecutionWorkItemState.OwnerProofRecorded or
                DataRightsExecutionWorkItemState.Completed))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ExecutionOwnerResultInvalid);
        }

        DataRightsProcessingLedgerEntry? ledger =
            await ledgers.GetByWorkItemAsync(
                workItem.Id,
                cancellationToken).ConfigureAwait(false);
        bool addLedger = ledger is null;
        if (ledger is null)
        {
            Result<DataRightsProcessingLedgerEntry> created =
                await this.CreateLedgerAsync(
                    workItem,
                    cancellationToken).ConfigureAwait(false);
            if (created.IsFailure)
            {
                return Result.Failure<Unit>(created.Error);
            }

            ledger = created.Value;
        }
        else if (!ledger.MatchesExecutionProof(workItem))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ProcessingLedgerConflict);
        }

        Result<DataRightsProtectedReplayEnvelope> envelope =
            replayProtector.Protect(ledger.Freeze(), workItem.RecordId);
        if (envelope.IsFailure)
        {
            return Result.Failure<Unit>(envelope.Error);
        }

        DataRightsLedgerDeltaAppendReceipt receipt =
            await deltaStore.AppendAsync(
                DataRightsLedgerDelta.Create(ledger, envelope.Value),
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasMatchingDurabilityProof(receipt, ledger))
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ProcessingLedgerDurabilityInvalid);
        }

        if (addLedger)
        {
            await ledgers.AddAsync(
                ledger,
                cancellationToken).ConfigureAwait(false);
        }

        bool wasCompleted =
            workItem.State == DataRightsExecutionWorkItemState.Completed;
        DateTimeOffset nowUtc = clock.UtcNow;
        Result completed = workItem.CompleteAfterDurableLedger(
            workItem.Version,
            nowUtc);
        if (completed.IsFailure)
        {
            return Result.Failure<Unit>(completed.Error);
        }

        if (!wasCompleted)
        {
            IOutboxWriter outbox =
                outboxWriters.GetRequired(DataRightsModuleMetadata.Name);
            if (workItem.PropertyId is Guid propertyId)
            {
                await outbox.EnqueueAsync(
                    new DataRightsAnonymisationWorkItemTerminalIntegrationEvent(
                            ids.NewId(),
                            workItem.ScopeId,
                            nowUtc,
                            workItem.BatchId,
                            workItem.Id,
                            workItem.CaseId,
                            propertyId,
                            workItem.ExecutionRevision),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (command.Scope.CaseType == DataRightsCaseType.StaffRights)
            {
                await outbox.EnqueueAsync(
                    new DataRightsAnonymisationWorkItemTerminalIntegrationEventV2(
                        ids.NewId(),
                        workItem.ScopeId,
                        nowUtc,
                        workItem.BatchId,
                        workItem.Id,
                        workItem.CaseId,
                        command.Scope.CaseType,
                        DataRightsExecutionScopeKind.Tenant,
                        propertyId: null,
                        workItem.ExecutionRevision),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return Result.Failure<Unit>(
                    DataRightsApplicationErrors.ExecutionCoordinateInvalid);
            }
        }

        return Result.Success(Unit.Value);
    }

    private async Task<Result<DataRightsProcessingLedgerEntry>>
        CreateLedgerAsync(
            DataRightsExecutionWorkItem workItem,
            CancellationToken cancellationToken)
    {
        Result<DataRightsRecordPseudonym> pseudonym =
            pseudonymizer.CreateActive(
                workItem.ScopeId,
                workItem.OwnerKey,
                workItem.RecordType,
                workItem.RecordId);
        if (pseudonym.IsFailure)
        {
            return Result.Failure<DataRightsProcessingLedgerEntry>(
                pseudonym.Error);
        }

        DataRightsProcessingLedgerEntry? latest =
            await ledgers.GetLatestAsync(cancellationToken)
                .ConfigureAwait(false);
        long sequence = latest is null ? 1 : latest.TenantSequence + 1;
        string previousEntrySha256 = latest?.EntrySha256 ??
            DataRightsProcessingLedgerEntry.GenesisEntrySha256;
        Guid entryId = DataRightsProcessingLedgerIdentity.Create(
            workItem.ScopeId,
            workItem.Id,
            workItem.OwnerReceiptId!.Value,
            workItem.OwnerReceiptSha256!);
        return DataRightsProcessingLedgerEntry.Create(
            entryId,
            sequence,
            workItem,
            pseudonym.Value,
            previousEntrySha256);
    }

    private static bool HasMatchingDurabilityProof(
        DataRightsLedgerDeltaAppendReceipt receipt,
        DataRightsProcessingLedgerEntry ledger) =>
        receipt.ContractVersion ==
            DataRightsLedgerDeltaAppendReceipt.CurrentContractVersion &&
        receipt.LedgerEntryId == ledger.Id &&
        receipt.Cursor.TenantSequence == ledger.TenantSequence &&
        string.Equals(
            receipt.Cursor.EntrySha256,
            ledger.EntrySha256,
            StringComparison.Ordinal) &&
        IsSha256(receipt.Cursor.StorageMacSha256) &&
        IsSha256(receipt.DurabilityProofSha256) &&
        receipt.FlushedAtUtc != default;

    private static bool IsSha256(string? value) =>
        value is { Length: DataRightsProcessingLedgerEntry.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
