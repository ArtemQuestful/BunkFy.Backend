namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

public sealed partial class TenantTerminationProcess
{
    public Result RecordBlocked(
        TenantTerminationProcessPhase phase,
        long operationRevision,
        string blockerCode,
        DateTimeOffset? holdReviewAtUtc,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedCode = blockerCode?.Trim() ?? string.Empty;
        string normalizedActor = NormalizeActor(actorId);
        if (phase == this.Phase &&
            operationRevision == this.OperationRevision &&
            this.Status == TenantTerminationProcessStatus.Blocked &&
            string.Equals(
                normalizedCode,
                this.OutcomeCode,
                StringComparison.Ordinal) &&
            holdReviewAtUtc == this.HoldReviewAtUtc &&
            string.Equals(
                normalizedActor,
                this.LastChangedBy,
                StringComparison.Ordinal) &&
            nowUtc == this.LastChangedAtUtc)
        {
            return Result.Success();
        }

        Result ready = this.ValidateRunningOutcome(
            phase,
            operationRevision,
            normalizedCode,
            expectedVersion,
            actorId,
            nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (holdReviewAtUtc.HasValue &&
            (holdReviewAtUtc.Value == default ||
             holdReviewAtUtc.Value < nowUtc))
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        this.Status = TenantTerminationProcessStatus.Blocked;
        this.OutcomeCode = normalizedCode;
        this.HoldReviewAtUtc = holdReviewAtUtc;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result RecordFailed(
        TenantTerminationProcessPhase phase,
        long operationRevision,
        string failureCode,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedCode = failureCode?.Trim() ?? string.Empty;
        string normalizedActor = NormalizeActor(actorId);
        if (phase == this.Phase &&
            operationRevision == this.OperationRevision &&
            this.Status == TenantTerminationProcessStatus.Failed &&
            string.Equals(
                normalizedCode,
                this.OutcomeCode,
                StringComparison.Ordinal) &&
            string.Equals(
                normalizedActor,
                this.LastChangedBy,
                StringComparison.Ordinal) &&
            nowUtc == this.LastChangedAtUtc)
        {
            return Result.Success();
        }

        Result ready = this.ValidateRunningOutcome(
            phase,
            operationRevision,
            normalizedCode,
            expectedVersion,
            actorId,
            nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        this.Status = TenantTerminationProcessStatus.Failed;
        this.OutcomeCode = normalizedCode;
        this.HoldReviewAtUtc = null;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result Requeue(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.ValidateChange(expectedVersion, actorId, nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.Status is not TenantTerminationProcessStatus.Blocked and
            not TenantTerminationProcessStatus.Failed)
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        if (this.Phase == TenantTerminationProcessPhase.Export)
        {
            this.ClearExportConfirmation();
        }

        if (this.Phase == TenantTerminationProcessPhase.Verify)
        {
            this.ClearVerificationConfirmation();
        }

        this.Status = TenantTerminationProcessStatus.Running;
        this.ClearOutcome();
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result RequestExportRegeneration(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedActor = NormalizeActor(actorId);
        if (this.Phase == TenantTerminationProcessPhase.Export &&
            this.Status == TenantTerminationProcessStatus.Pending &&
            string.Equals(
                normalizedActor,
                this.LastChangedBy,
                StringComparison.Ordinal) &&
            nowUtc == this.LastChangedAtUtc)
        {
            return Result.Success();
        }

        Result ready = this.ValidateChange(expectedVersion, actorId, nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        bool expiredBlock =
            this.Status == TenantTerminationProcessStatus.Blocked &&
            (string.Equals(
                 this.OutcomeCode,
                 ExportArtifactExpiredOutcomeCode,
                 StringComparison.Ordinal) ||
             string.Equals(
                 this.OutcomeCode,
                 ExportFragmentExpiredOutcomeCode,
                 StringComparison.Ordinal));
        if (this.Phase != TenantTerminationProcessPhase.Export ||
            (this.Status != TenantTerminationProcessStatus.Running &&
             !expiredBlock) ||
            this.HasCurrentExportConfirmation())
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        this.ClearExportConfirmation();
        this.Status = TenantTerminationProcessStatus.Pending;
        this.ClearOutcome();
        this.CompleteChange(normalizedActor, nowUtc);
        return Result.Success();
    }

    private Result ValidateRunningOutcome(
        TenantTerminationProcessPhase phase,
        long operationRevision,
        string outcomeCode,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.ValidateChange(expectedVersion, actorId, nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        string normalizedCode = outcomeCode?.Trim() ?? string.Empty;
        return phase == this.Phase &&
            operationRevision == this.OperationRevision &&
            this.Status == TenantTerminationProcessStatus.Running &&
            IsStableCode(normalizedCode, OutcomeCodeMaxLength)
                ? Result.Success()
                : Result.Failure(
                    DataRightsDomainErrors.TenantTerminationTransitionInvalid);
    }
}
