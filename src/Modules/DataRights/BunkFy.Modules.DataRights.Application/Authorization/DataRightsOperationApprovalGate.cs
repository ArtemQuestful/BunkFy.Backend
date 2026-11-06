namespace BunkFy.Modules.DataRights.Application.Authorization;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Application.Security;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Observability;
using SelectedSubject = Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class DataRightsOperationApprovalGate(
    IDataRightsCaseRepository cases,
    ISecuritySignalRecorder securitySignals)
    : IDataRightsOperationApprovalGate
{
    public async Task<DataRightsOperationApprovalResult> EvaluateAsync(
        DataRightsOperationApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidate(
                request,
                out string? tenantId,
                out string? ownerKey,
                out string? recordType,
                out DataRightsCaseScope? scope))
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.InvalidRequest);
        }

        DataRightsCase? dataRightsCase = await cases.GetAsync(
            scope!,
            request.CaseId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (dataRightsCase is null ||
            !string.Equals(dataRightsCase.ScopeId, tenantId, StringComparison.Ordinal))
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.CaseNotFound);
        }

        if (dataRightsCase.Decision != DataRightsCaseDecision.Approved ||
            dataRightsCase.Status is not DataRightsCaseState.Approved
                and not DataRightsCaseState.Executing)
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.CaseNotApproved);
        }

        if (dataRightsCase.DecisionRevision != request.ApprovalRevision)
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.ApprovalRevisionMismatch);
        }

        if ((((DataRightsOperation)dataRightsCase.RequestedOperations) & request.Operation) !=
            request.Operation)
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.OperationNotApproved);
        }

        if (request.Operation == DataRightsOperation.Restriction &&
            (DataRightsRestrictionDirective)dataRightsCase.RestrictionAction !=
                request.RestrictionDirective)
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.RestrictionDirectiveMismatch);
        }

        if (request.Operation == DataRightsOperation.Restriction &&
            !MatchesRestrictionTarget(dataRightsCase, request))
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.RestrictionTargetMismatch);
        }

        if (request.Operation == DataRightsOperation.Anonymisation)
        {
            DataRightsApprovalEvidence? approvalEvidence = dataRightsCase.ToApprovalEvidence();
            if (approvalEvidence is null)
            {
                return this.Denied(
                    DataRightsOperationApprovalDenial.ApprovalEvidenceMissing);
            }

            string executingActor = request.ExecutingActorId?.Trim() ?? string.Empty;
            if (executingActor.Length is 0 or > DataRightsCase.ActorIdMaxLength)
            {
                return this.Denied(
                    DataRightsOperationApprovalDenial.ExecutionActorRequired);
            }

            if (approvalEvidence.RequiresDistinctExecutor &&
                string.Equals(executingActor, dataRightsCase.DecidedBy, StringComparison.Ordinal))
            {
                return this.Denied(
                    DataRightsOperationApprovalDenial.DecisionActorCannotExecute);
            }
        }

        bool subjectApproved = dataRightsCase.SelectedSubjects.Any(subject =>
            string.Equals(subject.OwnerKey, ownerKey, StringComparison.Ordinal) &&
            string.Equals(subject.RecordType, recordType, StringComparison.Ordinal) &&
            subject.RecordId == request.RecordId &&
            subject.RecordVersion == request.RecordVersion);
        if (!subjectApproved)
        {
            return this.Denied(
                DataRightsOperationApprovalDenial.SubjectNotApproved);
        }

        return dataRightsCase.ToApprovalEvidence() is { } evidence
            ? DataRightsOperationApprovalResult.ApprovedWithEvidence(evidence)
            : DataRightsOperationApprovalResult.Approved;
    }

    private DataRightsOperationApprovalResult Denied(
        DataRightsOperationApprovalDenial denial)
    {
        securitySignals.Record(
            DataRightsApprovalSecuritySignalDefinitions.OperationApprovalDenied);
        return DataRightsOperationApprovalResult.Denied(denial);
    }

    private static bool TryValidate(
        DataRightsOperationApprovalRequest request,
        out string? tenantId,
        out string? ownerKey,
        out string? recordType,
        out DataRightsCaseScope? scope)
    {
        bool tenantValid = TenantIds.TryNormalize(request.TenantId, out tenantId);
        bool scopeValid = DataRightsCaseScope.TryCreate(
            request.CaseType,
            request.PropertyId,
            out scope);
        ownerKey = request.OwnerKey?.Trim().ToLowerInvariant();
        recordType = request.RecordType?.Trim().ToLowerInvariant();
        int operation = (int)request.Operation;
        bool restrictionDirectiveValid = request.Operation == DataRightsOperation.Restriction
            ? request.RestrictionDirective is DataRightsRestrictionDirective.Apply
                or DataRightsRestrictionDirective.Release
            : request.RestrictionDirective == DataRightsRestrictionDirective.Unknown;
        bool restrictionTargetValid = request.Operation == DataRightsOperation.Restriction &&
            request.RestrictionDirective == DataRightsRestrictionDirective.Release
                ? (request.RestrictionTargetOwnerOperationId is null &&
                   request.RestrictionTargetOwnerOperationVersion is null) ||
                  (request.RestrictionTargetOwnerOperationId is Guid targetId &&
                   targetId != Guid.Empty &&
                   request.RestrictionTargetOwnerOperationVersion is long targetVersion &&
                   targetVersion is > 0 and < long.MaxValue)
                : request.RestrictionTargetOwnerOperationId is null &&
                  request.RestrictionTargetOwnerOperationVersion is null;
        return tenantValid &&
            scopeValid &&
            request.CaseId != Guid.Empty &&
            request.ApprovalRevision > 0 &&
            operation is > 0 and <= (int)DataRightsOperation.Anonymisation &&
            (operation & (operation - 1)) == 0 &&
            restrictionDirectiveValid &&
            restrictionTargetValid &&
            ownerKey is not null &&
            ownerKey.Length is > 0 and <= SelectedSubject.OwnerKeyMaxLength &&
            recordType is not null &&
            recordType.Length is > 0 and <= SelectedSubject.RecordTypeMaxLength &&
            request.RecordId != Guid.Empty &&
            request.RecordVersion > 0;
    }

    private static bool MatchesRestrictionTarget(
        DataRightsCase dataRightsCase,
        DataRightsOperationApprovalRequest request)
    {
        if (request.RestrictionDirective == DataRightsRestrictionDirective.Apply)
        {
            return dataRightsCase.RestrictionTargetingContractVersion is null &&
                dataRightsCase.RestrictionReleaseTarget is null &&
                request.RestrictionTargetOwnerOperationId is null &&
                request.RestrictionTargetOwnerOperationVersion is null;
        }

        if (dataRightsCase.RestrictionTargetingContractVersion is null)
        {
            return dataRightsCase.RestrictionReleaseTarget is null &&
                request.RestrictionTargetOwnerOperationId is null &&
                request.RestrictionTargetOwnerOperationVersion is null;
        }

        return dataRightsCase.RestrictionTargetingContractVersion ==
                Domain.ValueObjects.DataRightsRestrictionReleaseTarget
                    .CurrentBindingVersion &&
            dataRightsCase.RestrictionReleaseTarget is { } target &&
            request.RestrictionTargetOwnerOperationId is Guid targetId &&
            request.RestrictionTargetOwnerOperationVersion is long targetVersion &&
            target.Matches(request.OwnerKey, targetId, targetVersion);
    }
}
