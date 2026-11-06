namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal sealed record DataRightsRestrictionTargetSet(
    IReadOnlyCollection<DataRightsRestrictionReleaseTarget> Targets,
    bool LimitReached);

internal static class DataRightsRestrictionTargetResolution
{
    public static Result<DataRightsRestrictionTargetSet> Validate(
        DataRightsRestrictionTargetResolutionResult? result,
        DateTimeOffset observedAtUtc,
        Guid? requestedOperationId = null,
        long? requestedOperationVersion = null)
    {
        bool exactRequest = requestedOperationId is not null ||
            requestedOperationVersion is not null;
        if (observedAtUtc == default ||
            result is null ||
            result.ContractVersion != DataRightsRestrictionContract.CurrentVersion ||
            exactRequest != (requestedOperationId is Guid id &&
                id != Guid.Empty &&
                requestedOperationVersion is long version &&
                version > 0))
        {
            return Invalid();
        }

        if (result.Status == DataRightsRestrictionTargetResolutionStatus.Blocked)
        {
            return HasFailureShape(result)
                ? Result.Failure<DataRightsRestrictionTargetSet>(
                    DataRightsApplicationErrors.RestrictionExecutionBlocked)
                : Invalid();
        }

        if (result.Status == DataRightsRestrictionTargetResolutionStatus.Failed)
        {
            return HasFailureShape(result)
                ? Result.Failure<DataRightsRestrictionTargetSet>(
                    DataRightsApplicationErrors.RestrictionOwnerRetryRequired)
                : Invalid();
        }

        if (result.Status is DataRightsRestrictionTargetResolutionStatus.NotFound
            or DataRightsRestrictionTargetResolutionStatus.Stale)
        {
            if (!exactRequest ||
                result.Targets is not null ||
                result.LimitReached ||
                result.OutcomeCode is not null)
            {
                return Invalid();
            }

            return Result.Failure<DataRightsRestrictionTargetSet>(
                result.Status == DataRightsRestrictionTargetResolutionStatus.NotFound
                    ? DataRightsApplicationErrors.RestrictionReleaseTargetNotFound
                    : DataRightsApplicationErrors.RestrictionReleaseTargetStale);
        }

        if (result.Status != DataRightsRestrictionTargetResolutionStatus.Completed ||
            result.Targets is null ||
            result.OutcomeCode is not null ||
            result.Targets.Count > DataRightsRestrictionContract.MaxReleaseTargets ||
            (result.LimitReached &&
             result.Targets.Count != DataRightsRestrictionContract.MaxReleaseTargets))
        {
            return Invalid();
        }

        DataRightsRestrictionReleaseTarget[] targets = result.Targets.ToArray();
        if (targets.Any(target => !IsValid(target, observedAtUtc)) ||
            targets.GroupBy(target => target.OwnerOperationId).Any(group => group.Count() != 1) ||
            (exactRequest &&
             (targets.Length != 1 ||
              result.LimitReached ||
              targets[0].OwnerOperationId != requestedOperationId ||
              targets[0].OwnerOperationVersion != requestedOperationVersion)))
        {
            return Invalid();
        }

        DataRightsRestrictionReleaseTarget[] ordered = targets
            .OrderBy(target => target.AppliedAtUtc)
            .ThenBy(target => target.OwnerOperationId)
            .ToArray();
        return Result.Success(new DataRightsRestrictionTargetSet(
            ordered,
            result.LimitReached));
    }

    private static bool HasFailureShape(
        DataRightsRestrictionTargetResolutionResult result) =>
        result.Targets is null &&
        !result.LimitReached &&
        result.OutcomeCode is { } code &&
        code.Length is > 0 and <= DataRightsRestrictionContract.CodeMaxLength &&
        string.Equals(code, code.Trim(), StringComparison.Ordinal);

    private static bool IsValid(
        DataRightsRestrictionReleaseTarget? target,
        DateTimeOffset observedAtUtc) =>
        target is not null &&
        target.OwnerOperationId != Guid.Empty &&
        target.OwnerOperationVersion is > 0 and < long.MaxValue &&
        target.SourceCaseId != Guid.Empty &&
        target.AppliedAtUtc != default &&
        target.AppliedAtUtc <= observedAtUtc;

    private static Result<DataRightsRestrictionTargetSet> Invalid() =>
        Result.Failure<DataRightsRestrictionTargetSet>(
            DataRightsApplicationErrors.RestrictionReleaseTargetResultInvalid);
}
