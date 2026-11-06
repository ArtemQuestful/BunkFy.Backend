namespace BunkFy.Modules.Reservations.Domain.StayAmendments;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Errors;
using Gma.Framework.Results;

public sealed partial class ReservationStayAmendmentOperation
{
    public Result Reconcile(
        long expectedOperationVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result valid = this.CanReconcile(expectedOperationVersion, actorId, nowUtc);
        if (valid.IsFailure)
        {
            return valid;
        }

        string normalizedActor = actorId!.Trim();
        this.ReconciliationCount++;
        this.LastReconciledAtUtc = nowUtc;
        this.LastReconciledBy = normalizedActor;
        this.Advance(nowUtc);
        return Result.Success();
    }

    public Result CanReconcile(
        long expectedOperationVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (expectedOperationVersion != this.OperationVersion)
        {
            return Result.Failure(ReservationsDomainErrors.StayAmendmentOperationVersionConflict);
        }

        string normalizedActor = actorId?.Trim() ?? string.Empty;
        if (this.Outcome != ReservationStayAmendmentOperationOutcome.Pending ||
            normalizedActor.Length is 0 or > Reservation.ActorIdMaxLength ||
            normalizedActor.Any(char.IsControl) ||
            !this.IsValidAdvance(nowUtc) || this.ReconciliationCount == int.MaxValue)
        {
            return Result.Failure(ReservationsDomainErrors.StayAmendmentOperationTransitionInvalid);
        }

        return this.IsReconciliationEligible(nowUtc)
            ? Result.Success()
            : Result.Failure(ReservationsDomainErrors.StayAmendmentReconcileTooSoon);
    }
}
