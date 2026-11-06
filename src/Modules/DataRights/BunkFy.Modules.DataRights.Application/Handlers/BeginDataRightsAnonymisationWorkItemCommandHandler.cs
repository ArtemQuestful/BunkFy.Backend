namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class BeginDataRightsAnonymisationWorkItemCommandHandler(
    DataRightsExecutionMutationCoordinator mutations,
    IDataRightsExecutionWorkItemRepository workItems,
    IDataRightsOperationApprovalGate approvalGate,
    IOutboxWriterRegistry outboxWriters,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        BeginDataRightsAnonymisationWorkItemCommand,
        DataRightsAnonymisationWorkItemStart>
{
    internal static readonly TimeSpan OwnerCallTimeout = TimeSpan.FromMinutes(2);
    private const string ApprovalBlockedCode = "DataRights.ApprovalRevalidationDenied";
    private const string ApprovalEvidenceChangedCode =
        "DataRights.ApprovalEvidenceChanged";

    public async Task<Result<DataRightsAnonymisationWorkItemStart>> HandleAsync(
        BeginDataRightsAnonymisationWorkItemCommand command,
        CancellationToken cancellationToken)
    {
        DataRightsCase? dataRightsCase = await mutations.AcquireWorkItemAsync(
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
            return Result.Failure<DataRightsAnonymisationWorkItemStart>(
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
            dataRightsCase.ExecutionRevision != command.ExecutionRevision)
        {
            return Result.Failure<DataRightsAnonymisationWorkItemStart>(
                DataRightsApplicationErrors.ExecutionCoordinateInvalid);
        }

        if (workItem.IsOwnerDispatchTerminal)
        {
            return Result.Success(
                DataRightsAnonymisationWorkItemStart.Terminal(workItem.Version));
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result processing = workItem.BeginProcessing(
            command.TaskRunId,
            command.TaskAttempt,
            nowUtc);
        if (processing.IsFailure)
        {
            return Result.Failure<DataRightsAnonymisationWorkItemStart>(
                processing.Error);
        }

        DataRightsOperationApprovalResult approval = await approvalGate.EvaluateAsync(
            new DataRightsOperationApprovalRequest(
                dataRightsCase.ScopeId,
                command.Scope.PropertyId,
                command.CaseId,
                command.ApprovalRevision,
                DataRightsOperation.Anonymisation,
                workItem.OwnerKey,
                workItem.RecordType,
                workItem.RecordId,
                workItem.SelectedRecordVersion,
                ExecutingActorId: workItem.CreatedBy,
                CaseType: command.Scope.CaseType),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.IsApproved || approval.ApprovalEvidence is null)
        {
            return await this.BlockAsync(
                workItem,
                command,
                ApprovalBlockedCode,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
        }

        DataRightsApprovalEvidence evidence = approval.ApprovalEvidence;
        if (dataRightsCase.ApprovalPolicyEvidence is not { } frozenEvidence ||
            !DataRightsApprovalEvidenceComparer.Matches(frozenEvidence, evidence) ||
            !workItem.MatchesPolicyEvidence(frozenEvidence))
        {
            return await this.BlockAsync(
                workItem,
                command,
                ApprovalEvidenceChangedCode,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
        }

        DataRightsSubjectCoordinate coordinate = new(
            workItem.OwnerKey,
            workItem.RecordType,
            workItem.RecordId,
            workItem.SelectedRecordVersion);
        if (workItem.OwnerContractVersion ==
                DataRightsExecutionWorkItem.PropertyOwnerContractVersion &&
            command.Scope.PropertyId is Guid routingPropertyId)
        {
            DataRightsAnonymisationContributionRequest propertyRequest = new(
                workItem.OwnerContractVersion,
                dataRightsCase.ScopeId,
                workItem.Id,
                workItem.IdempotencyKey,
                routingPropertyId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExecutionRevision,
                coordinate,
                evidence,
                workItem.CreatedBy,
                nowUtc.Add(OwnerCallTimeout));
            return Result.Success(
                DataRightsAnonymisationWorkItemStart.Ready(
                    workItem.Version,
                    propertyRequest));
        }

        if (workItem.OwnerContractVersion ==
                DataRightsExecutionWorkItem.ScopedOwnerContractVersion &&
            command.Scope.CaseType == DataRightsCaseType.StaffRights &&
            command.Scope.PropertyId is null)
        {
            DataRightsAnonymisationContributionRequestV2 scopedRequest = new(
                workItem.OwnerContractVersion,
                dataRightsCase.ScopeId,
                command.Scope.CaseType,
                DataRightsExecutionScopeKind.Tenant,
                PropertyId: null,
                workItem.Id,
                workItem.IdempotencyKey,
                command.CaseId,
                command.ApprovalRevision,
                command.ExecutionRevision,
                coordinate,
                evidence,
                workItem.CreatedBy,
                nowUtc.Add(OwnerCallTimeout));
            return Result.Success(
                DataRightsAnonymisationWorkItemStart.Ready(
                    workItem.Version,
                    scopedRequest));
        }

        return Result.Failure<DataRightsAnonymisationWorkItemStart>(
            DataRightsApplicationErrors.ExecutionCoordinateInvalid);
    }

    private async Task<Result<DataRightsAnonymisationWorkItemStart>> BlockAsync(
        DataRightsExecutionWorkItem workItem,
        BeginDataRightsAnonymisationWorkItemCommand command,
        string blockerCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Result blocked = workItem.RecordBlocked(
            workItem.Version,
            command.TaskRunId,
            command.TaskAttempt,
            blockerCode,
            nowUtc);
        if (blocked.IsFailure)
        {
            return Result.Failure<DataRightsAnonymisationWorkItemStart>(blocked.Error);
        }

        IOutboxWriter outbox =
            outboxWriters.GetRequired(DataRightsModuleMetadata.Name);
        if (command.Scope.PropertyId is Guid propertyId)
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
            return Result.Failure<DataRightsAnonymisationWorkItemStart>(
                DataRightsApplicationErrors.ExecutionCoordinateInvalid);
        }

        return Result.Success(
            DataRightsAnonymisationWorkItemStart.Terminal(workItem.Version));
    }

}
