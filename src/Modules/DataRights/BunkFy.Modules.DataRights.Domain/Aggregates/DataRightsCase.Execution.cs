namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed partial class DataRightsCase
{
    public Result CompleteRestrictionExecution(
        long expectedVersion,
        DataRightsRestrictionExecutionProof proof,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proof);

        DataRightsSubjectCoordinate? subject = this.selectedSubjects.Count == 1
            ? this.selectedSubjects[0]
            : null;
        if (this.Status == DataRightsCaseState.Completed &&
            this.RestrictionExecutionProof is not null &&
            subject is not null)
        {
            return this.RestrictionExecutionProof.Matches(
                proof.IdempotencyKey,
                proof.ApprovalRevision,
                proof.Directive,
                subject,
                proof.ExecutedBy,
                this.RestrictionReleaseTarget)
                ? Result.Success()
                : Result.Failure(DataRightsDomainErrors.RestrictionExecutionConflict);
        }

        Result ready = this.EnsureTransition(
            expectedVersion,
            proof.ExecutedBy,
            nowUtc,
            DataRightsCaseState.Approved);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.Decision != DataRightsCaseDecision.Approved ||
            this.DecisionReason != DataRightsCaseDecisionReason.RequestValidated ||
            this.RequestedOperations != DataRightsCaseOperation.Restriction ||
            this.DecisionRevision != proof.ApprovalRevision ||
            this.RestrictionAction != proof.Directive ||
            subject is null ||
            !proof.Matches(
                proof.IdempotencyKey,
                proof.ApprovalRevision,
                proof.Directive,
                subject,
                proof.ExecutedBy,
                this.RestrictionReleaseTarget) ||
            proof.CompletedAtUtc > nowUtc)
        {
            return Result.Failure(DataRightsDomainErrors.RestrictionExecutionInvalid);
        }

        this.ExecutionStartedBy = proof.ExecutedBy;
        this.ExecutionStartedAtUtc = proof.CompletedAtUtc;
        this.RestrictionExecutionProof = proof;
        this.Status = DataRightsCaseState.Completed;
        this.CompleteChange(proof.ExecutedBy, nowUtc);
        this.ExecutionRevision = this.Version;
        return Result.Success();
    }

    public Result CompleteAccessExport(
        long decisionRevision,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (this.Status == DataRightsCaseState.Completed &&
            this.DecisionRevision == decisionRevision &&
            this.RequestedOperations == DataRightsCaseOperation.AccessExport)
        {
            return Result.Success();
        }

        Result ready = this.EnsureTransition(
            this.Version,
            actorId,
            nowUtc,
            DataRightsCaseState.Approved);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.Decision != DataRightsCaseDecision.Approved ||
            this.DecisionReason !=
                DataRightsCaseDecisionReason.RequestValidated ||
            this.RequestedOperations !=
                DataRightsCaseOperation.AccessExport ||
            this.DecisionRevision != decisionRevision)
        {
            return Result.Failure(
                DataRightsDomainErrors.AccessExportCompletionInvalid);
        }

        this.Status = DataRightsCaseState.Completed;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result BeginAnonymisationExecution(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Approved);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.Decision != DataRightsCaseDecision.Approved ||
            this.RequestedOperations != DataRightsCaseOperation.Anonymisation ||
            this.DecisionRevision is null ||
            this.ApprovalPolicyEvidence is null)
        {
            return Result.Failure(DataRightsDomainErrors.AnonymisationApprovalInvalid);
        }

        if (this.selectedSubjects.Count is <= 0 or > MaxSelectedSubjects)
        {
            return Result.Failure(DataRightsDomainErrors.AnonymisationSubjectCountInvalid);
        }

        string normalizedActor = actorId.Trim();
        if (this.ApprovalPolicyEvidence.RequiresDistinctExecutor &&
            string.Equals(normalizedActor, this.DecidedBy, StringComparison.Ordinal))
        {
            return Result.Failure(DataRightsDomainErrors.DecisionActorCannotExecute);
        }

        this.Status = DataRightsCaseState.Executing;
        this.ExecutionStartedBy = normalizedActor;
        this.ExecutionStartedAtUtc = nowUtc;
        this.CompleteChange(normalizedActor, nowUtc);
        this.ExecutionRevision = this.Version;
        return Result.Success();
    }

    public Result ReconcileAnonymisationExecution(
        long expectedVersion,
        int totalCount,
        int completedCount,
        int noOpCount,
        int blockedCount,
        int failedCount,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (totalCount <= 0 ||
            totalCount != this.selectedSubjects.Count ||
            completedCount < 0 ||
            noOpCount < 0 ||
            blockedCount < 0 ||
            failedCount < 0 ||
            completedCount + noOpCount + blockedCount + failedCount != totalCount)
        {
            return Result.Failure(DataRightsDomainErrors.ExecutionOutcomeInvalid);
        }

        int successfulCount = completedCount + noOpCount;
        DataRightsCaseState target = successfulCount == totalCount
            ? DataRightsCaseState.Completed
            : successfulCount == 0
                ? DataRightsCaseState.Blocked
                : DataRightsCaseState.PartiallyCompleted;
        if (this.Status == target)
        {
            return Result.Success();
        }

        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Executing);
        if (ready.IsFailure)
        {
            return ready;
        }

        this.Status = target;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }
}
