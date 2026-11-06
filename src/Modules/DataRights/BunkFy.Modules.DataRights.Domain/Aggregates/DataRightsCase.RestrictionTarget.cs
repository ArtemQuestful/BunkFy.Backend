namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed partial class DataRightsCase
{
    public Result SelectRestrictionReleaseTarget(
        string ownerKey,
        Guid ownerOperationId,
        long ownerOperationVersion,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Discovery);
        if (ready.IsFailure)
        {
            return ready;
        }

        string owner = ownerKey?.Trim() ?? string.Empty;
        Domain.Entities.DataRightsSubjectCoordinate? subject =
            this.selectedSubjects.Count == 1
                ? this.selectedSubjects[0]
                : null;
        if (this.RequestedOperations != DataRightsCaseOperation.Restriction ||
            this.RestrictionAction != DataRightsRestrictionAction.Release ||
            this.RestrictionTargetingContractVersion !=
                DataRightsRestrictionReleaseTarget.CurrentBindingVersion ||
            subject is null ||
            !string.Equals(
                subject.OwnerKey,
                owner,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(
                DataRightsDomainErrors.RestrictionReleaseTargetInvalid);
        }

        Result<DataRightsRestrictionReleaseTarget> target =
            DataRightsRestrictionReleaseTarget.Create(
                owner,
                ownerOperationId,
                ownerOperationVersion,
                actorId,
                nowUtc);
        if (target.IsFailure)
        {
            return Result.Failure(target.Error);
        }

        this.RestrictionReleaseTarget = target.Value;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    private void ClearRestrictionReleaseTarget() =>
        this.RestrictionReleaseTarget = null;
}
