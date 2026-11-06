namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Policies;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ApplyStaffRetentionCommandHandler(
    IStaffRetentionExecutionRepository executions,
    IStaffRetentionCandidateRepository candidates,
    IStaffMemberRepository members,
    IStaffOperationLock operationLock,
    IStaffMemberMutationOperationRepository memberMutationOperations,
    IStaffIdentityProvisioningAnchorResolutionRepository resolutions,
    StaffRetentionEligibilityEvaluator eligibility,
    StaffRetentionPrerequisiteEvaluator prerequisites,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ApplyStaffRetentionCommand,
        StaffRetentionMutationResult>
{
    public async Task<Result<StaffRetentionMutationResult>> HandleAsync(
        ApplyStaffRetentionCommand command,
        CancellationToken cancellationToken)
    {
        string? tenantId =
            scopeContext.IsEnabled ? scopeContext.ScopeId : null;
        if (string.IsNullOrWhiteSpace(tenantId) ||
            command.ExecutionId == Guid.Empty ||
            command.Attempt < 1 ||
            command.StaffMemberId == Guid.Empty ||
            command.ExpectedStaffVersion < 1)
        {
            return Result.Failure<StaffRetentionMutationResult>(
                StaffApplicationErrors.RetentionMutationInvalid);
        }

        StaffRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null ||
            !string.Equals(
                execution.ScopeId,
                tenantId,
                StringComparison.Ordinal) ||
            execution.State != StaffRetentionExecutionState.Running ||
            execution.Attempt != command.Attempt ||
            !execution.MatchesCoordinate(
                StaffRetentionCoordinates.DataClassKey,
                StaffRetentionCoordinates.ExecutionPolicyVersion))
        {
            return Result.Failure<StaffRetentionMutationResult>(
                StaffApplicationErrors.RetentionExecutionNotFound);
        }

        StaffRetentionAnonymisationReceipt? existing =
            await executions.GetReceiptAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            StaffAnonymisationTombstone? existingTombstone =
                await executions.GetTombstoneAsync(
                    command.StaffMemberId,
                    cancellationToken).ConfigureAwait(false);
            if (existingTombstone is null ||
                !existingTombstone.MatchesRetention(existing))
            {
                return Result.Failure<StaffRetentionMutationResult>(
                    StaffApplicationErrors.RetentionProofConflict);
            }

            return Result.Success(
                new StaffRetentionMutationResult(
                    existing.Matches(
                        command.ExecutionId,
                        command.StaffMemberId,
                        command.ExpectedStaffVersion)
                        ? StaffRetentionMutationStatus.AlreadyApplied
                        : StaffRetentionMutationStatus
                            .NoLongerEligible));
        }

        bool lockAcquired =
            await operationLock.TryAcquireStaffMemberAsync(
                tenantId,
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            return Result.Success(
                new StaffRetentionMutationResult(
                    StaffRetentionMutationStatus.NoLongerEligible));
        }

        long? resultingLockRevision =
            await operationLock.GetStaffMemberRevisionAsync(
                tenantId,
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (resultingLockRevision is null or <= 1)
        {
            return Result.Success(Failed(
                StaffRetentionMutationFailure
                    .ProjectionUnavailable));
        }

        if (await resolutions.HasUnresolvedWorkspaceOnboardingAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(Failed(
                StaffRetentionMutationFailure
                    .IdentityAnchorResolutionRequired));
        }

        StaffRetentionCandidateSnapshot? snapshot =
            await candidates.LoadAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.StaffVersion !=
                command.ExpectedStaffVersion)
        {
            return Result.Success(
                new StaffRetentionMutationResult(
                    StaffRetentionMutationStatus.NoLongerEligible));
        }

        DateTimeOffset nowUtc =
            ToPersistencePrecision(clock.UtcNow);
        StaffRetentionEligibilityResult decision =
            eligibility.Evaluate(snapshot, nowUtc);
        if (decision.Status ==
            StaffRetentionEligibilityStatus.NotDue)
        {
            return Result.Success(
                new StaffRetentionMutationResult(
                    StaffRetentionMutationStatus.NoLongerEligible));
        }

        if (decision.Status ==
            StaffRetentionEligibilityStatus.Blocked)
        {
            return Result.Success(
                new StaffRetentionMutationResult(
                    StaffRetentionMutationStatus.Blocked,
                    decision.HoldReviewDueAtUtc));
        }

        if (decision.Status !=
                StaffRetentionEligibilityStatus.Eligible ||
            !decision.DepartedAtUtc.HasValue ||
            !decision.RetentionDeadlineUtc.HasValue ||
            string.IsNullOrWhiteSpace(
                decision.PolicyEvidenceSha256) ||
            snapshot.OperationLockRevision !=
                resultingLockRevision)
        {
            return Result.Success(Failed(
                decision.Code switch
                {
                    StaffRetentionEligibilityCode
                            .ProjectionUnavailable =>
                        StaffRetentionMutationFailure
                            .ProjectionUnavailable,
                    StaffRetentionEligibilityCode.PolicyUnavailable =>
                        StaffRetentionMutationFailure
                            .PolicyUnavailable,
                    _ => StaffRetentionMutationFailure.MutationFailed
                }));
        }

        StaffRetentionPrerequisiteEvaluation prerequisite =
            await prerequisites.VerifyAsync(
                new(
                    StaffRetentionAnonymisationPrerequisiteContract
                        .CurrentVersion,
                    command.ExecutionId,
                    tenantId,
                    command.StaffMemberId,
                    command.ExpectedStaffVersion),
                cancellationToken).ConfigureAwait(false);
        if (prerequisite !=
            StaffRetentionPrerequisiteEvaluation.Completed)
        {
            return Result.Success(Failed(
                prerequisite ==
                    StaffRetentionPrerequisiteEvaluation.Blocked
                    ? StaffRetentionMutationFailure
                        .PrerequisiteBlocked
                    : StaffRetentionMutationFailure
                        .PrerequisiteUnavailable));
        }

        StaffMember? member = await members.GetForDataRightsAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (member is null ||
            member.Version != command.ExpectedStaffVersion ||
            member.Status != StaffMemberState.Departed ||
            !member.DepartedAtUtc.HasValue ||
            member.DepartedAtUtc.Value.ToUniversalTime() !=
                decision.DepartedAtUtc.Value.ToUniversalTime() ||
            member.Assignments.Any(assignment =>
                assignment.IsCurrent))
        {
            return Result.Success(
                new StaffRetentionMutationResult(
                    StaffRetentionMutationStatus.NoLongerEligible));
        }

        Result<StaffMemberAnonymisationOutcome> mutated =
            member.Anonymise(
                command.ExpectedStaffVersion,
                StaffRetentionCoordinates.SystemActor,
                ids.NewId(),
                nowUtc);
        if (mutated.IsFailure)
        {
            return Result.Failure<StaffRetentionMutationResult>(
                mutated.Error);
        }

        Result<StaffRetentionAnonymisationReceipt> receipt =
            StaffRetentionAnonymisationReceipt.Create(
                ids.NewId(),
                tenantId,
                command.ExecutionId,
                command.StaffMemberId,
                mutated.Value,
                resultingLockRevision.Value - 1,
                resultingLockRevision.Value,
                decision.DepartedAtUtc.Value,
                decision.RetentionDeadlineUtc.Value,
                decision.PolicyEvidenceSha256);
        if (receipt.IsFailure)
        {
            return Result.Failure<StaffRetentionMutationResult>(
                receipt.Error);
        }

        Result<StaffAnonymisationTombstone> tombstone =
            StaffAnonymisationTombstone.CreateForRetention(
                receipt.Value);
        if (tombstone.IsFailure)
        {
            return Result.Failure<StaffRetentionMutationResult>(
                tombstone.Error);
        }

        Result recorded = execution.RecordAffected();
        if (recorded.IsFailure)
        {
            return Result.Failure<StaffRetentionMutationResult>(
                recorded.Error);
        }

        await memberMutationOperations.DeleteForStaffMemberAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        await executions.AddAnonymisationProofAsync(
            receipt.Value,
            tombstone.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(
            new StaffRetentionMutationResult(
                StaffRetentionMutationStatus.Applied));
    }

    private static StaffRetentionMutationResult Failed(
        StaffRetentionMutationFailure failure) =>
        new(
            StaffRetentionMutationStatus.Failed,
            Failure: failure);

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
