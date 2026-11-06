namespace BunkFy.Modules.DataRights.Application.Authorization;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Runtime.Time;

internal sealed class DataRightsCorrectionExecutionGate(
    IDataRightsCaseRepository cases,
    IDataRightsCorrectionExecutionRepository executions,
    ISystemClock clock)
    : IDataRightsCorrectionExecutionGate
{
    public async Task<DataRightsCorrectionExecutionGateResult> EvaluateAsync(
        DataRightsCorrectionExecutionGateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TenantIds.TryNormalize(request.TenantId, out string? tenantId) ||
            !DataRightsCaseScope.TryCreate(
                request.CaseType,
                request.PropertyId,
                out DataRightsCaseScope? scope) ||
            request.CaseId == Guid.Empty ||
            request.ApprovalRevision < 1 ||
            request.ExecutionId == Guid.Empty ||
            request.Coordinate is null ||
            request.Coordinate.RecordId == Guid.Empty ||
            request.Coordinate.RecordVersion < 1)
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.InvalidRequest);
        }

        DataRightsCorrectionExecution? execution = await executions.GetAsync(
            request.CaseId,
            request.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (execution is null ||
            !string.Equals(execution.ScopeId, tenantId, StringComparison.Ordinal))
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.ExecutionNotFound);
        }

        if (execution.State == DataRightsCorrectionExecutionState.Completed)
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.ExecutionCompleted);
        }

        DataRightsCase? dataRightsCase = await cases.GetAsync(
            scope!,
            request.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null ||
            dataRightsCase.Status != DataRightsCaseState.Executing ||
            dataRightsCase.ExecutionRevision != execution.ExecutionRevision)
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.CaseNotExecuting);
        }

        if (execution.ApprovalRevision != request.ApprovalRevision)
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.ApprovalRevisionMismatch);
        }

        if (!execution.MatchesAuthorization(
                (DataRightsCaseKind)request.CaseType,
                request.PropertyId,
                request.CaseId,
                request.ApprovalRevision,
                request.ExecutionId,
                request.Coordinate.OwnerKey,
                request.Coordinate.RecordType,
                request.Coordinate.RecordId,
                request.Coordinate.RecordVersion,
                request.FieldPolicyKey,
                request.ExecutingActorId))
        {
            return DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.ExecutionMismatch);
        }

        return clock.UtcNow >= execution.ExpiresAtUtc
            ? DataRightsCorrectionExecutionGateResult.Denied(
                DataRightsCorrectionExecutionDenial.ExecutionExpired)
            : DataRightsCorrectionExecutionGateResult.Allowed(execution.ExpiresAtUtc);
    }
}
