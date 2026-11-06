namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class
    ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler(
        IWorkspaceStaffCorrelationAnonymisationRepository
            correlations,
        IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
            identityAnchors,
        IWorkspaceCrossGraphMutationLock crossGraphLock,
        WorkspaceStaffAccessMutationCoordinator mutations,
        IWorkspaceStaffCorrelationOperationLock operationLock,
        IDataRightsOperationApprovalGate approvalGate,
        IScopeContext scopeContext,
        ISystemClock clock,
        IIdGenerator ids)
    : ICommandHandler<
        ApplyWorkspaceStaffCorrelationAnonymisationCommand,
        WorkspaceStaffCorrelationAnonymisationReceiptDto>
{
    public async Task<Result<
        WorkspaceStaffCorrelationAnonymisationReceiptDto>>
        HandleAsync(
            ApplyWorkspaceStaffCorrelationAnonymisationCommand
                command,
            CancellationToken cancellationToken)
    {
        string? tenantId =
            scopeContext.IsEnabled ? scopeContext.ScopeId : null;
        string? actorId = NormalizeActor(command.ActorId);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .TenantRequired);
        }

        if (!IsValid(command, actorId, clock.UtcNow))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .RequestInvalid);
        }

        string approvalEvidenceSha256 =
            WorkspaceStaffCorrelationAnonymisationPolicyEvidence
                .ComputeApprovalSha256(command.ApprovalEvidence);
        WorkspaceStaffCorrelationAnonymisationReceipt?
            existing =
            await correlations.FindReceiptByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await this.ReplayAsync(
                existing,
                command,
                approvalEvidenceSha256,
                actorId!,
                cancellationToken).ConfigureAwait(false);
        }

        DataRightsOperationApprovalResult approval =
            await approvalGate.EvaluateAsync(
                new DataRightsOperationApprovalRequest(
                    tenantId,
                    PropertyId: null,
                    command.CaseId,
                    command.ApprovalRevision,
                    DataRightsOperation.Anonymisation,
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffAccessProcessRecordType,
                    command.AnchorProcessId,
                    command.ExpectedAnchorVersion,
                    ExecutingActorId: actorId,
                    CaseType: DataRightsCaseType.StaffRights),
                cancellationToken).ConfigureAwait(false);
        if (!approval.IsApproved ||
            approval.ApprovalEvidence is null ||
            !WorkspaceStaffCorrelationAnonymisationPolicyEvidence
                .IsValid(approval.ApprovalEvidence) ||
            !string.Equals(
                approvalEvidenceSha256,
                WorkspaceStaffCorrelationAnonymisationPolicyEvidence
                    .ComputeApprovalSha256(
                        approval.ApprovalEvidence),
                StringComparison.Ordinal))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .ApprovalRequired);
        }

        await crossGraphLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await mutations.TryAcquireExistingCoordinateAsync(
                command.AnchorProcessId,
                cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .OperationLockUnavailable);
        }

        bool locked = await operationLock.TryAcquireAsync(
            command.AnchorProcessId,
            cancellationToken).ConfigureAwait(false);
        if (!locked)
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .OperationLockUnavailable);
        }

        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            await correlations.ReadAsync(
                tenantId,
                command.AnchorProcessId,
                command.ExpectedAnchorVersion,
                cancellationToken).ConfigureAwait(false);
        if (snapshot.Status !=
                WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                    .Eligible ||
            snapshot.AnchorProcessId != command.AnchorProcessId ||
            snapshot.AnchorProcessVersion !=
                command.ExpectedAnchorVersion)
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .NotEligible);
        }

        if (!WorkspaceStaffCorrelationAnonymisationPolicyEvidence
                .MatchesFrozenBinding(
                    command.ApprovalEvidence,
                    snapshot))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .StateChanged);
        }

        if (!await identityAnchors.CanMutateAsync(
                tenantId,
                snapshot.SubjectId,
                cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .IdentityAnchorUnavailable);
        }

        string? stateBindingSha256 =
            WorkspaceStaffCorrelationAnonymisationPolicyEvidence
                .GetFrozenBindingSha256(
                    command.ApprovalEvidence);
        if (stateBindingSha256 is null)
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .StateChanged);
        }

        Result<WorkspaceStaffCorrelationAnonymisationReceipt>
            applied = await correlations.ApplyAsync(
                new(
                    ids.NewId(),
                    tenantId,
                    command.IdempotencyKey,
                    command.CaseId,
                    command.ApprovalRevision,
                    command.OperationRevision,
                    snapshot,
                    approvalEvidenceSha256,
                    stateBindingSha256,
                    actorId!,
                    ToPersistencePrecision(clock.UtcNow)),
                cancellationToken).ConfigureAwait(false);
        return applied.IsFailure
            ? Failure(applied.Error)
            : Result.Success(applied.Value.ToDto());
    }

    private async Task<Result<
        WorkspaceStaffCorrelationAnonymisationReceiptDto>>
        ReplayAsync(
            WorkspaceStaffCorrelationAnonymisationReceipt receipt,
            ApplyWorkspaceStaffCorrelationAnonymisationCommand
                command,
            string approvalEvidenceSha256,
            string actorId,
            CancellationToken cancellationToken)
    {
        if (!receipt.Matches(
                command.IdempotencyKey,
                command.CaseId,
                command.ApprovalRevision,
                command.OperationRevision,
                command.AnchorProcessId,
                command.ExpectedAnchorVersion,
                approvalEvidenceSha256,
                actorId))
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .IdempotencyConflict);
        }

        WorkspaceStaffCorrelationAnonymisationTombstone?
            tombstone = await correlations.GetTombstoneAsync(
                command.AnchorProcessId,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            await correlations.ReadAnonymisedAsync(
                receipt.ScopeId,
                receipt.AnchorProcessId,
                receipt.ResultingAnchorVersion,
                receipt.Id,
                cancellationToken).ConfigureAwait(false);
        if (tombstone is null ||
            !tombstone.Matches(receipt) ||
            snapshot.Status !=
                WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                    .Eligible ||
            !string.Equals(
                snapshot.StateSha256,
                receipt.ResultingStateSha256,
                StringComparison.Ordinal) ||
            snapshot.OnboardingRecordCount !=
                receipt.OnboardingRecordsScrubbed ||
            snapshot.AccessProcessRecordCount !=
                receipt.AccessProcessRecordsScrubbed ||
            snapshot.AccessPlanRecordCount !=
                receipt.AccessPlanRecordsScrubbed)
        {
            return Failure(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .ProofUnavailable);
        }

        return Result.Success(receipt.ToDto());
    }

    private static bool IsValid(
        ApplyWorkspaceStaffCorrelationAnonymisationCommand command,
        string? actorId,
        DateTimeOffset nowUtc) =>
        command.IdempotencyKey != Guid.Empty &&
        command.CaseId != Guid.Empty &&
        command.ApprovalRevision > 0 &&
        command.OperationRevision > command.ApprovalRevision &&
        command.AnchorProcessId != Guid.Empty &&
        command.ExpectedAnchorVersion > 0 &&
        actorId is not null &&
        WorkspaceStaffCorrelationAnonymisationPolicyEvidence
            .IsValid(command.ApprovalEvidence) &&
        command.ApprovalEvidence.RetentionDeadlineUtc <=
            nowUtc.ToUniversalTime();

    private static string? NormalizeActor(string? actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            WorkspaceStaffAccessProcess.ActorIdMaxLength
            ? normalized
            : null;
    }

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }

    private static Result<
        WorkspaceStaffCorrelationAnonymisationReceiptDto> Failure(
            Error error) =>
        Result.Failure<
            WorkspaceStaffCorrelationAnonymisationReceiptDto>(error);
}
