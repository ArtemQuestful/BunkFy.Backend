namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ApplyStaffAnonymisationCommandHandler(
    IStaffMemberRepository members,
    IStaffEmploymentGovernanceRepository governanceRepository,
    IStaffProcessingRestrictionProjectionRepository restrictionProjections,
    IStaffDataHoldRepository holds,
    IStaffOperationLock operationLock,
    IStaffAnonymisationRepository anonymisation,
    IStaffMemberMutationOperationRepository memberMutationOperations,
    IStaffIdentityProvisioningAnchorResolutionRepository resolutions,
    IDataRightsOperationApprovalGate approvalGate,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ApplyStaffAnonymisationCommand,
        StaffAnonymisationReceiptDto>
{
    public async Task<Result<StaffAnonymisationReceiptDto>> HandleAsync(
        ApplyStaffAnonymisationCommand command,
        CancellationToken cancellationToken)
    {
        string? tenantId =
            scopeContext.IsEnabled ? scopeContext.ScopeId : null;
        string? actorId = NormalizeActor(command.ActorId);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.TenantRequired);
        }

        if (!IsValid(command, actorId))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.AnonymisationRequestInvalid);
        }

        string approvalEvidenceSha256 =
            StaffAnonymisationPolicyEvidence.ComputeApprovalSha256(
                command.ApprovalEvidence);
        StaffAnonymisationReceipt? existing =
            await anonymisation.FindReceiptByIdempotencyKeyAsync(
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
                    StaffDataRightsCoordinates.Owner,
                    StaffDataRightsCoordinates.StaffMemberRecordType,
                    command.StaffMemberId,
                    command.ExpectedStaffVersion,
                    ExecutingActorId: actorId,
                    CaseType: DataRightsCaseType.StaffRights),
                cancellationToken).ConfigureAwait(false);
        if (!approval.IsApproved ||
            approval.ApprovalEvidence is null ||
            !StaffAnonymisationPolicyEvidence.IsValidStaffApproval(
                approval.ApprovalEvidence) ||
            !string.Equals(
                approvalEvidenceSha256,
                StaffAnonymisationPolicyEvidence.ComputeApprovalSha256(
                    approval.ApprovalEvidence),
                StringComparison.Ordinal))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.AnonymisationApprovalRequired);
        }

        bool lockAcquired =
            await operationLock.TryAcquireStaffMemberAsync(
                tenantId,
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.StaffMemberNotFound);
        }

        long? resultingLockRevision =
            await operationLock.GetStaffMemberRevisionAsync(
                tenantId,
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (resultingLockRevision is null or <= 1)
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors
                    .AnonymisationOperationLockUnavailable);
        }

        if (await resolutions.HasUnresolvedWorkspaceOnboardingAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.IdentityAnchorResolutionRequired);
        }

        StaffMember? member = await members.GetForDataRightsAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        StaffEmploymentGovernance? governance =
            await governanceRepository.GetAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        StaffProcessingRestrictionProjection? restriction =
            await restrictionProjections.GetAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<StaffDataHold>? holdSnapshot =
            await StaffDataHoldSnapshotReader.ReadAsync(
                tenantId,
                command.StaffMemberId,
                holds,
                cancellationToken).ConfigureAwait(false);

        if (!HasEligibleState(
                tenantId,
                command,
                member,
                governance,
                restriction,
                holdSnapshot,
                clock.UtcNow))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.AnonymisationNotEligible);
        }

        long selectedLockRevision =
            resultingLockRevision.Value - 1;
        if (!StaffAnonymisationPolicyEvidence.MatchesFrozenBindings(
                command.ApprovalEvidence,
                member!,
                governance!,
                restriction!,
                holdSnapshot!,
                selectedLockRevision))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.AnonymisationStateChanged);
        }

        DateTimeOffset nowUtc =
            ToPersistencePrecision(clock.UtcNow);
        Guid eventId = ids.NewId();
        Result<StaffMemberAnonymisationOutcome> mutated =
            member!.Anonymise(
                command.ExpectedStaffVersion,
                actorId!,
                eventId,
                nowUtc);
        if (mutated.IsFailure)
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                mutated.Error);
        }

        Result<StaffAnonymisationReceipt> receipt =
            StaffAnonymisationReceipt.Create(
                ids.NewId(),
                tenantId,
                command.IdempotencyKey,
                command.CaseId,
                command.ApprovalRevision,
                command.OperationRevision,
                command.StaffMemberId,
                mutated.Value.PreviousVersion,
                mutated.Value.CurrentVersion,
                selectedLockRevision,
                resultingLockRevision.Value,
                approvalEvidenceSha256,
                command.ApprovalEvidence.StateBindingsSha256!,
                mutated.Value.EventId,
                actorId!,
                mutated.Value.OccurredAtUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                receipt.Error);
        }

        Result<StaffAnonymisationTombstone> tombstone =
            StaffAnonymisationTombstone.Create(
                tenantId,
                command.StaffMemberId,
                receipt.Value.CompletedAtUtc,
                receipt.Value.CanonicalSha256);
        if (tombstone.IsFailure)
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                tombstone.Error);
        }

        await memberMutationOperations.DeleteForStaffMemberAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        await anonymisation.AddAsync(
            receipt.Value,
            tombstone.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private async Task<Result<StaffAnonymisationReceiptDto>> ReplayAsync(
        StaffAnonymisationReceipt receipt,
        ApplyStaffAnonymisationCommand command,
        string approvalEvidenceSha256,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!receipt.Matches(
                command.IdempotencyKey,
                command.CaseId,
                command.ApprovalRevision,
                command.OperationRevision,
                command.StaffMemberId,
                command.ExpectedStaffVersion,
                approvalEvidenceSha256,
                actorId))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors
                    .AnonymisationIdempotencyConflict);
        }

        StaffMember? member = await members.GetForDataRightsAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        StaffAnonymisationTombstone? tombstone =
            await anonymisation.GetTombstoneAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (member is null ||
            !member.MatchesAnonymisedState(
                receipt.ResultingStaffVersion,
                receipt.CompletedAtUtc) ||
            tombstone is null ||
            !tombstone.Matches(receipt))
        {
            return Result.Failure<StaffAnonymisationReceiptDto>(
                StaffApplicationErrors.AnonymisationProofUnavailable);
        }

        return Result.Success(receipt.ToDto());
    }

    private static bool HasEligibleState(
        string tenantId,
        ApplyStaffAnonymisationCommand command,
        StaffMember? member,
        StaffEmploymentGovernance? governance,
        StaffProcessingRestrictionProjection? restriction,
        IReadOnlyCollection<StaffDataHold>? holds,
        DateTimeOffset nowUtc) =>
        member is not null &&
        governance is not null &&
        restriction is not null &&
        holds is not null &&
        string.Equals(
            member.ScopeId,
            tenantId,
            StringComparison.Ordinal) &&
        member.Id == command.StaffMemberId &&
        member.Version == command.ExpectedStaffVersion &&
        member.Status == StaffMemberState.Departed &&
        member.DepartedAtUtc.HasValue &&
        member.DepartureEffectiveOn.HasValue &&
        !member.Assignments.Any(assignment => assignment.IsCurrent) &&
        string.Equals(
            governance.ScopeId,
            tenantId,
            StringComparison.Ordinal) &&
        governance.StaffMemberId == member.Id &&
        governance.SelectedStaffVersion == member.Version &&
        string.Equals(
            restriction.ScopeId,
            tenantId,
            StringComparison.Ordinal) &&
        restriction.StaffMemberId == member.Id &&
        restriction.ContractVersion ==
            StaffProcessingRestrictionContract.CurrentVersion &&
        holds.All(hold =>
            hold.State != StaffDataHoldState.Active) &&
        command.ApprovalEvidence.RetentionTriggeredAtUtc ==
            member.DepartedAtUtc.Value.ToUniversalTime() &&
        command.ApprovalEvidence.RetentionDeadlineUtc <=
            nowUtc.ToUniversalTime();

    private static bool IsValid(
        ApplyStaffAnonymisationCommand command,
        string? actorId) =>
        command.IdempotencyKey != Guid.Empty &&
        command.CaseId != Guid.Empty &&
        command.ApprovalRevision > 0 &&
        command.OperationRevision > command.ApprovalRevision &&
        command.StaffMemberId != Guid.Empty &&
        command.ExpectedStaffVersion > 0 &&
        actorId is not null &&
        StaffAnonymisationPolicyEvidence.IsValidStaffApproval(
            command.ApprovalEvidence);

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= StaffMember.ActorIdMaxLength
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
}
