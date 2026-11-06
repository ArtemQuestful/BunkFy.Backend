namespace BunkFy.Modules.DataRights.Application.Mapping;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;

public static class DataRightsCaseMappings
{
    public static DataRightsCaseDto ToDto(this DataRightsCase dataRightsCase) => new(
        dataRightsCase.Id,
        dataRightsCase.PropertyId,
        (DataRightsCaseType)dataRightsCase.Kind,
        (DataRightsOperation)dataRightsCase.RequestedOperations,
        (DataRightsRestrictionDirective)dataRightsCase.RestrictionAction,
        (DataRightsRequesterRelationship)dataRightsCase.RequesterRelationship,
        (DataRightsVerificationStatus)dataRightsCase.VerificationStatus,
        (DataRightsRoutingStatus)dataRightsCase.RoutingStatus,
        (DataRightsCaseStatus)dataRightsCase.Status,
        (DataRightsDecisionOutcome)dataRightsCase.Decision,
        (DataRightsDecisionReason)dataRightsCase.DecisionReason,
        dataRightsCase.DecisionRevision,
        dataRightsCase.DecidedAtUtc,
        dataRightsCase.ExecutionRevision,
        dataRightsCase.ExecutionStartedAtUtc,
        dataRightsCase.SelectedSubjects.Count,
        dataRightsCase.DueAtUtc,
        dataRightsCase.Version,
        dataRightsCase.CreatedAtUtc,
        dataRightsCase.LastChangedAtUtc,
        dataRightsCase.ToApprovalEvidence(),
        dataRightsCase.ToResponseDeadlineEvidence(),
        dataRightsCase.RestrictionTargetingContractVersion,
        dataRightsCase.RestrictionReleaseTarget is null
            ? null
            : new DataRightsRestrictionReleaseTargetDto(
                dataRightsCase.RestrictionReleaseTarget.OwnerKey,
                dataRightsCase.RestrictionReleaseTarget.OwnerOperationId,
                dataRightsCase.RestrictionReleaseTarget.OwnerOperationVersion,
                dataRightsCase.RestrictionReleaseTarget.SelectedAtUtc),
        dataRightsCase.RestrictionExecutionProof?.ToDto());

    public static DataRightsResponseDeadlineEvidence? ToResponseDeadlineEvidence(
        this DataRightsCase dataRightsCase) =>
        dataRightsCase.ResponseDeadlinePolicyEvidence is null
            ? null
            : new DataRightsResponseDeadlineEvidence(
                dataRightsCase.ResponseDeadlinePolicyEvidence.SchemaVersion,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PropertyId,
                dataRightsCase.ResponseDeadlinePolicyEvidence
                    .PropertyTopologySourceVersion,
                dataRightsCase.ResponseDeadlinePolicyEvidence
                    .PropertyPolicySourceVersion,
                dataRightsCase.ResponseDeadlinePolicyEvidence
                    .OperatingCountryCode,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PolicyId,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PolicyVersion,
                dataRightsCase.ResponseDeadlinePolicyEvidence.ContentSha256,
                (DataRightsResponseDeadlineRight)
                    dataRightsCase.ResponseDeadlinePolicyEvidence
                        .ControllingRight,
                dataRightsCase.ResponseDeadlinePolicyEvidence.RuleReference,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PeriodYears,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PeriodMonths,
                dataRightsCase.ResponseDeadlinePolicyEvidence.PeriodDays,
                dataRightsCase.ResponseDeadlinePolicyEvidence.TimeZoneId,
                dataRightsCase.ResponseDeadlinePolicyEvidence
                    .PolicyEffectiveAtUtc,
                dataRightsCase.ResponseDeadlinePolicyEvidence
                    .PolicyExpiresAtUtc,
                dataRightsCase.ResponseDeadlinePolicyEvidence.ReceivedAtUtc,
                dataRightsCase.ResponseDeadlinePolicyEvidence.EvaluatedAtUtc,
                dataRightsCase.ResponseDeadlinePolicyEvidence.DueAtUtc);

    public static DataRightsApprovalEvidence? ToApprovalEvidence(
        this DataRightsCase dataRightsCase) =>
        dataRightsCase.ApprovalPolicyEvidence is null
            ? null
            : new DataRightsApprovalEvidence(
                dataRightsCase.ApprovalPolicyEvidence.SchemaVersion,
                dataRightsCase.ApprovalPolicyEvidence.PropertyId,
                dataRightsCase.ApprovalPolicyEvidence.PropertyVersion,
                dataRightsCase.ApprovalPolicyEvidence.OperatingCountryCode,
                dataRightsCase.ApprovalPolicyEvidence.PolicyId,
                dataRightsCase.ApprovalPolicyEvidence.PolicyVersion,
                dataRightsCase.ApprovalPolicyEvidence.RetentionPolicyId,
                dataRightsCase.ApprovalPolicyEvidence.RetentionPolicyVersion,
                dataRightsCase.ApprovalPolicyEvidence.ContentSha256,
                dataRightsCase.ApprovalPolicyEvidence.PurposeCode,
                dataRightsCase.ApprovalPolicyEvidence.Surface,
                dataRightsCase.ApprovalPolicyEvidence.SourceProvenance,
                dataRightsCase.ApprovalPolicyEvidence.EvaluatedAtUtc,
                dataRightsCase.ApprovalPolicyEvidence.RequiresDistinctExecutor,
                (DataRightsCaseType)
                    dataRightsCase.ApprovalPolicyEvidence.CaseKind,
                (DataRightsExecutionScopeKind)
                    dataRightsCase.ApprovalPolicyEvidence.ScopeKind,
                EmptyToNull(
                    dataRightsCase.ApprovalPolicyEvidence
                        .RetentionDataClass),
                EmptyToNull(
                    dataRightsCase.ApprovalPolicyEvidence.RetentionTrigger),
                dataRightsCase.ApprovalPolicyEvidence
                    .RetentionTriggeredAtUtc,
                dataRightsCase.ApprovalPolicyEvidence.RetentionDeadlineUtc,
                dataRightsCase.ApprovalPolicyEvidence.StateBindings
                    .Select(binding => new DataRightsApprovalEvidenceBinding(
                        binding.Key,
                        binding.Version,
                        binding.Sha256))
                    .ToArray(),
                dataRightsCase.ApprovalPolicyEvidence
                    .StateBindingsSha256);

    private static string? EmptyToNull(string value) =>
        value.Length == 0 ? null : value;
}
