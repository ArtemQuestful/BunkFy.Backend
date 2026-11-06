namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;

internal static class DataRightsExportGenerationCase
{
    public static bool IsEligible(
        DataRightsCase dataRightsCase,
        DataRightsCaseScope scope,
        long expectedVersion,
        IReadOnlyCollection<DataRightsSubjectCoordinate> subjects) =>
        dataRightsCase.Version == expectedVersion &&
        dataRightsCase.Status == DataRightsCaseState.Approved &&
        dataRightsCase.Decision == DataRightsCaseDecision.Approved &&
        dataRightsCase.DecisionReason ==
            DataRightsCaseDecisionReason.RequestValidated &&
        dataRightsCase.RequestedOperations ==
            DataRightsCaseOperation.AccessExport &&
        dataRightsCase.DecisionRevision is > 0 &&
        dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
        dataRightsCase.PropertyId == scope.PropertyId &&
        subjects.Count is > 0 and <= DataRightsCase.MaxSelectedSubjects;

    public static bool Matches(
        DataRightsCase dataRightsCase,
        DataRightsExportArtifact artifact,
        DataRightsCaseScope scope,
        Guid caseId,
        long decisionRevision,
        out DataRightsSubjectCoordinate[] subjects)
    {
        subjects = RequestDataRightsExportCommandHandler.ToCoordinates(dataRightsCase);
        return dataRightsCase.Id == caseId &&
            artifact.CaseId == caseId &&
            artifact.CaseKind == (DataRightsCaseKind)scope.CaseType &&
            artifact.PropertyId == scope.PropertyId &&
            artifact.DecisionRevision == decisionRevision &&
            dataRightsCase.DecisionRevision == decisionRevision &&
            dataRightsCase.Kind == artifact.CaseKind &&
            dataRightsCase.PropertyId == artifact.PropertyId &&
            dataRightsCase.Status == DataRightsCaseState.Approved &&
            dataRightsCase.Decision == DataRightsCaseDecision.Approved &&
            dataRightsCase.DecisionReason ==
                DataRightsCaseDecisionReason.RequestValidated &&
            dataRightsCase.RequestedOperations ==
                DataRightsCaseOperation.AccessExport &&
            subjects.Length == artifact.SelectedSubjectCount &&
            string.Equals(
                DataRightsExportIdentity.SelectionSha256(subjects),
                artifact.SelectionSha256,
                StringComparison.Ordinal);
    }
}
