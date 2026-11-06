namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;

internal static class DataRightsRestrictionReleaseTargetCase
{
    public static Result<DataRightsSubjectCoordinate> RequireSubject(
        DataRightsCase dataRightsCase,
        DataRightsCaseScope scope)
    {
        if (dataRightsCase.Kind != (DataRightsCaseKind)scope.CaseType ||
            dataRightsCase.PropertyId != scope.PropertyId ||
            dataRightsCase.RequestedOperations != DataRightsCaseOperation.Restriction ||
            dataRightsCase.RestrictionAction != DataRightsRestrictionAction.Release ||
            dataRightsCase.RestrictionTargetingContractVersion !=
                DataRightsRestrictionReleaseTarget.CurrentBindingVersion)
        {
            return Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors.RestrictionReleaseTargetInvalid);
        }

        if (dataRightsCase.Status != DataRightsCaseState.Discovery)
        {
            return Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors.TransitionInvalid);
        }

        return dataRightsCase.SelectedSubjects.Count == 1
            ? Result.Success(dataRightsCase.SelectedSubjects.Single())
            : Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors.SubjectSelectionRequired);
    }
}
