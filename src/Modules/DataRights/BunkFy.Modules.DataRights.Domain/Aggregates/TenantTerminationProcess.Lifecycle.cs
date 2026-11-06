namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

public sealed partial class TenantTerminationProcess
{
    public Result BeginPhase(
        TenantTerminationProcessPhase phase,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedActor = NormalizeActor(actorId);
        if (phase == this.Phase &&
            this.Status == TenantTerminationProcessStatus.Running &&
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

        if (phase != this.Phase ||
            this.Status != TenantTerminationProcessStatus.Pending ||
            this.OperationRevision == long.MaxValue ||
            phase is TenantTerminationProcessPhase.Unknown or
                TenantTerminationProcessPhase.Completed)
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        if (phase == TenantTerminationProcessPhase.Destroy &&
            string.Equals(normalizedActor, this.ApprovedBy, StringComparison.Ordinal))
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationExecutorInvalid);
        }

        if (phase == TenantTerminationProcessPhase.Export)
        {
            this.ClearExportConfirmation();
        }

        if (phase == TenantTerminationProcessPhase.Verify)
        {
            this.ClearVerificationConfirmation();
        }

        this.Status = TenantTerminationProcessStatus.Running;
        this.OperationRevision++;
        this.ClearOutcome();
        this.CompleteChange(normalizedActor, nowUtc);
        return Result.Success();
    }

    public Result RequestCancellation(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedActor = NormalizeActor(actorId);
        if (this.Phase == TenantTerminationProcessPhase.Restore &&
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

        bool cancellable =
            (this.Phase == TenantTerminationProcessPhase.Export &&
                this.Status is
                    TenantTerminationProcessStatus.Pending or
                    TenantTerminationProcessStatus.Running or
                    TenantTerminationProcessStatus.Blocked or
                    TenantTerminationProcessStatus.Failed) ||
            (this.Phase == TenantTerminationProcessPhase.Destroy &&
                this.Status == TenantTerminationProcessStatus.Pending);
        if (!cancellable)
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        this.Phase = TenantTerminationProcessPhase.Restore;
        this.Status = TenantTerminationProcessStatus.Pending;
        this.ClearOutcome();
        this.CompleteChange(normalizedActor, nowUtc);
        return Result.Success();
    }

    public Result CompleteCancellation(
        long operationRevision,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        string normalizedActor = NormalizeActor(actorId);
        if (this.Phase == TenantTerminationProcessPhase.Restore &&
            this.Status == TenantTerminationProcessStatus.Cancelled &&
            operationRevision == this.OperationRevision &&
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

        if (this.Phase != TenantTerminationProcessPhase.Restore ||
            this.Status != TenantTerminationProcessStatus.Running ||
            operationRevision != this.OperationRevision)
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        this.Status = TenantTerminationProcessStatus.Cancelled;
        this.ClearOutcome();
        this.CompleteChange(normalizedActor, nowUtc);
        return Result.Success();
    }

    public Result CompletePhase(
        TenantTerminationProcessPhase phase,
        long operationRevision,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        TenantTerminationProcessPhase nextPhase = this.GetNextPhase(phase);
        string normalizedActor = NormalizeActor(actorId);
        bool isReplayTarget = this.Status is
            TenantTerminationProcessStatus.Pending or
            TenantTerminationProcessStatus.Completed;
        if (operationRevision == this.OperationRevision &&
            nextPhase == this.Phase &&
            isReplayTarget &&
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

        if (phase != this.Phase ||
            operationRevision != this.OperationRevision ||
            this.Status != TenantTerminationProcessStatus.Running ||
            phase is TenantTerminationProcessPhase.Freeze or
                TenantTerminationProcessPhase.Restore ||
            (phase == TenantTerminationProcessPhase.Export &&
             !this.HasCurrentExportConfirmation()) ||
            (phase == TenantTerminationProcessPhase.Verify &&
             !this.HasCurrentVerificationConfirmation()))
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        if (nextPhase == TenantTerminationProcessPhase.Unknown)
        {
            return Result.Failure(
                DataRightsDomainErrors.TenantTerminationTransitionInvalid);
        }

        if (phase == TenantTerminationProcessPhase.Destroy)
        {
            this.DestroyCompletedOperationRevision = operationRevision;
            this.DestroyedAtUtc = nowUtc;
        }

        this.ClearOutcome();
        this.Phase = nextPhase;
        this.Status = this.Phase == TenantTerminationProcessPhase.Completed
            ? TenantTerminationProcessStatus.Completed
            : TenantTerminationProcessStatus.Pending;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    private TenantTerminationProcessPhase GetNextPhase(
        TenantTerminationProcessPhase phase) =>
        phase switch
        {
            TenantTerminationProcessPhase.Freeze when this.ExportRequested =>
                TenantTerminationProcessPhase.Export,
            TenantTerminationProcessPhase.Freeze =>
                TenantTerminationProcessPhase.Destroy,
            TenantTerminationProcessPhase.Export =>
                TenantTerminationProcessPhase.Destroy,
            TenantTerminationProcessPhase.Destroy =>
                TenantTerminationProcessPhase.Verify,
            TenantTerminationProcessPhase.Verify =>
                TenantTerminationProcessPhase.Completed,
            _ => TenantTerminationProcessPhase.Unknown
        };

    private Result ValidateChange(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(DataRightsDomainErrors.VersionConflict);
        }

        return NormalizeActor(actorId).Length == 0 ||
            nowUtc == default ||
            nowUtc < this.LastChangedAtUtc
                ? Result.Failure(
                    DataRightsDomainErrors.TenantTerminationTransitionInvalid)
                : Result.Success();
    }

    private void CompleteChange(string actorId, DateTimeOffset nowUtc)
    {
        this.LastChangedBy = actorId.Trim();
        this.LastChangedAtUtc = nowUtc;
        this.Version++;
    }

    private void ClearOutcome()
    {
        this.OutcomeCode = null;
        this.HoldReviewAtUtc = null;
    }
}
