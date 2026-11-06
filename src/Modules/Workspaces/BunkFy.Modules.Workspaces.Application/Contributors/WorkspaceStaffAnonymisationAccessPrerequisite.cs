namespace BunkFy.Modules.Workspaces.Application.Contributors;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.Extensions.Logging;

internal sealed class WorkspaceStaffAnonymisationAccessPrerequisite(
    IStaffAnonymisationRestoreStateReader staffStateReader,
    IWorkspaceStaffAccessProcessRepository accessProcesses,
    IWorkspaceStaffRetentionCorrelationRepository correlations,
    IWorkspaceStaffCorrelationAnonymisationRepository
        dataRightsCorrelations,
    IRequestDispatcher dispatcher,
    WorkspaceStaffAccessDenier accessDenier,
    ILogger<WorkspaceStaffAnonymisationAccessPrerequisite> logger)
    : IDataRightsAnonymisationExecutionPrerequisiteV2,
      IDataRightsAnonymisationRestorePrerequisiteV3,
      IStaffRetentionAnonymisationPrerequisite
{
    private const string RetentionContributorKey =
        "workspace-access";
    private const string RequestInvalid =
        WorkspaceStaffAnonymisationAccessCodes.RequestInvalid;
    private const string StateUnavailable =
        WorkspaceStaffAnonymisationAccessCodes.StateUnavailable;
    private const string StateConflict =
        WorkspaceStaffAnonymisationAccessCodes.StateConflict;
    private const string AccessMappingUnavailable =
        WorkspaceStaffAnonymisationAccessCodes.AccessMappingUnavailable;
    private const string AccessMappingConflict =
        WorkspaceStaffAnonymisationAccessCodes.AccessMappingConflict;
    private const string OwnerProtected =
        WorkspaceStaffAnonymisationAccessCodes.OwnerProtected;
    private const string RetryRequired =
        WorkspaceStaffAnonymisationAccessCodes.RetryRequired;
    private const string CorrelationReceiptInvalid =
        WorkspaceStaffAnonymisationAccessCodes.CorrelationReceiptInvalid;
    private const string CorrelationScrubRetryRequired =
        WorkspaceStaffAnonymisationAccessCodes
            .CorrelationScrubRetryRequired;

    public string OwnerKey => StaffDataRightsCoordinates.Owner;

    public string RecordType =>
        StaffDataRightsCoordinates.StaffMemberRecordType;

    public DataRightsCaseType CaseType =>
        DataRightsCaseType.StaffRights;

    public string ContributorKey => RetentionContributorKey;

    int IDataRightsAnonymisationExecutionPrerequisiteV2.ContractVersion =>
        DataRightsAnonymisationExecutionPrerequisiteContractV2
            .CurrentVersion;

    int IDataRightsAnonymisationRestorePrerequisiteV3.ContractVersion =>
        DataRightsAnonymisationRestoreContractV3.CurrentVersion;

    public async Task<DataRightsAnonymisationExecutionPrerequisiteResult>
        ExecuteAsync(
            DataRightsAnonymisationContributionRequestV2 request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return ExecutionBlocked(RequestInvalid);
        }

        WorkspaceStaffAccessClosureResult result =
            await this.EnsureClosedAsync(
            request.TenantId,
            request.Coordinate.RecordId,
            request.Coordinate.RecordVersion,
            resultingRecordVersion: null,
            acceptDataRightsCorrelationProof: true,
            cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            WorkspaceStaffAccessClosureStatus.Completed =>
                DataRightsAnonymisationExecutionPrerequisiteResult.Completed(
                    DataRightsAnonymisationExecutionPrerequisiteContractV2
                        .CurrentVersion),
            WorkspaceStaffAccessClosureStatus.Blocked =>
                ExecutionBlocked(result.Code),
            _ =>
                DataRightsAnonymisationExecutionPrerequisiteResult
                    .RetryRequired(
                        DataRightsAnonymisationExecutionPrerequisiteContractV2
                            .CurrentVersion,
                        result.Code)
        };
    }

    public async Task<DataRightsAnonymisationRestorePrerequisiteResult>
        ExecuteAsync(
            DataRightsAnonymisationRestoreRequestV3 request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return RestoreBlocked(RequestInvalid);
        }

        WorkspaceStaffAccessClosureResult result =
            await this.EnsureClosedAsync(
            request.TenantId,
            request.RecordId,
            request.ResultingRecordVersion - 1,
            request.ResultingRecordVersion,
            acceptDataRightsCorrelationProof: true,
            cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            WorkspaceStaffAccessClosureStatus.Completed =>
                DataRightsAnonymisationRestorePrerequisiteResult.Completed(
                    DataRightsAnonymisationRestoreContractV3.CurrentVersion),
            WorkspaceStaffAccessClosureStatus.Blocked =>
                RestoreBlocked(result.Code),
            _ =>
                DataRightsAnonymisationRestorePrerequisiteResult
                    .RetryRequired(
                        DataRightsAnonymisationRestoreContractV3
                            .CurrentVersion,
                        result.Code)
        };
    }

    public async Task<StaffRetentionAnonymisationPrerequisiteResult>
        PrepareAsync(
            StaffRetentionAnonymisationPrerequisiteRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return StaffRetentionAnonymisationPrerequisiteResult.Blocked(
                RequestInvalid);
        }

        try
        {
            WorkspaceStaffRetentionCorrelationReceipt? existing =
                await correlations.GetAsync(
                    request.StaffMemberId,
                    request.SelectedStaffVersion,
                    cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.Matches(
                        request.TenantId,
                        request.StaffMemberId,
                        request.SelectedStaffVersion))
                {
                    return StaffRetentionAnonymisationPrerequisiteResult
                        .Blocked(CorrelationReceiptInvalid);
                }

                return StaffRetentionAnonymisationPrerequisiteResult
                    .Completed();
            }

            Result<WorkspaceStaffRetentionCorrelationReceipt> scrubbed =
                await dispatcher.SendAsync(
                    new ScrubWorkspaceStaffRetentionCorrelationCommand(
                        request.ExecutionId,
                        request.TenantId,
                        request.StaffMemberId,
                        request.SelectedStaffVersion),
                    cancellationToken).ConfigureAwait(false);
            if (scrubbed.IsSuccess)
            {
                return StaffRetentionAnonymisationPrerequisiteResult
                    .Completed();
            }

            return IsBlockedScrubFailure(scrubbed.Error)
                ? StaffRetentionAnonymisationPrerequisiteResult
                    .Blocked(scrubbed.Error.Code)
                : StaffRetentionAnonymisationPrerequisiteResult
                    .RetryRequired(
                        CorrelationScrubRetryRequired);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Workspace Staff retention correlation scrub failed because {ExceptionType} was raised.",
                exception.GetType().Name);
            return StaffRetentionAnonymisationPrerequisiteResult
                .RetryRequired(CorrelationScrubRetryRequired);
        }
    }

    public async Task<StaffRetentionAnonymisationPrerequisiteResult>
        VerifyAsync(
            StaffRetentionAnonymisationPrerequisiteRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return StaffRetentionAnonymisationPrerequisiteResult.Blocked(
                RequestInvalid);
        }

        try
        {
            WorkspaceStaffRetentionCorrelationReceipt? receipt =
                await correlations.GetAsync(
                    request.StaffMemberId,
                    request.SelectedStaffVersion,
                    cancellationToken).ConfigureAwait(false);
            if (receipt is null)
            {
                return StaffRetentionAnonymisationPrerequisiteResult
                    .RetryRequired(CorrelationScrubRetryRequired);
            }

            return receipt.Matches(
                    request.TenantId,
                    request.StaffMemberId,
                    request.SelectedStaffVersion)
                ? StaffRetentionAnonymisationPrerequisiteResult.Completed()
                : StaffRetentionAnonymisationPrerequisiteResult.Blocked(
                    CorrelationReceiptInvalid);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Workspace Staff retention correlation proof verification failed because {ExceptionType} was raised.",
                exception.GetType().Name);
            return StaffRetentionAnonymisationPrerequisiteResult
                .RetryRequired(CorrelationScrubRetryRequired);
        }
    }

    private async Task<WorkspaceStaffAccessClosureResult> EnsureClosedAsync(
        string tenantId,
        Guid staffMemberId,
        long departureVersion,
        long? resultingRecordVersion,
        bool acceptDataRightsCorrelationProof,
        CancellationToken cancellationToken)
    {
        try
        {
            StaffAnonymisationRestoreState? state =
                await staffStateReader.ReadAsync(
                    tenantId,
                    staffMemberId,
                    cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return WorkspaceStaffAccessClosureResult.Blocked(
                    StateUnavailable);
            }

            string? subjectId;
            if (state.State ==
                    StaffAnonymisationRestoreRecordState.Departed &&
                state.Version == departureVersion)
            {
                subjectId = state.AuthSubjectId;
                if (string.IsNullOrWhiteSpace(subjectId))
                {
                    return WorkspaceStaffAccessClosureResult.Complete(
                        subjectId: null);
                }
            }
            else if (resultingRecordVersion.HasValue &&
                     state.State ==
                        StaffAnonymisationRestoreRecordState.Anonymised &&
                     state.Version == resultingRecordVersion.Value &&
                     state.AuthSubjectId is null)
            {
                subjectId = null;
            }
            else
            {
                return WorkspaceStaffAccessClosureResult.Blocked(
                    StateConflict);
            }

            if (acceptDataRightsCorrelationProof)
            {
                WorkspaceStaffCorrelationAnonymisationTombstone?
                    proof = await dataRightsCorrelations
                        .FindTombstoneAsync(
                            tenantId,
                            staffMemberId,
                            departureVersion,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (proof is not null)
                {
                    if (!proof.HasValidProof())
                    {
                        return WorkspaceStaffAccessClosureResult.Blocked(
                            AccessMappingConflict);
                    }

                    WorkspaceStaffCorrelationAnonymisationSnapshot
                        anonymised = await dataRightsCorrelations
                            .ReadAnonymisedAsync(
                                tenantId,
                                proof.Id,
                                proof.ResultingAnchorVersion,
                                proof.OwnerReceiptId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    return anonymised.Status ==
                            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                                .Eligible &&
                        anonymised.StaffMemberId ==
                            staffMemberId &&
                        anonymised.SelectedStaffVersion ==
                            departureVersion &&
                        string.Equals(
                            anonymised.StateSha256,
                            proof.ResultingStateSha256,
                            StringComparison.Ordinal)
                        ? WorkspaceStaffAccessClosureResult.Complete(
                            subjectId: null)
                        : WorkspaceStaffAccessClosureResult.Blocked(
                            AccessMappingConflict);
                }
            }

            WorkspaceStaffAccessProcess? process =
                await accessProcesses.GetCompletedDepartureAsync(
                    staffMemberId,
                    departureVersion,
                    cancellationToken).ConfigureAwait(false);
            if (process is null)
            {
                return WorkspaceStaffAccessClosureResult.Blocked(
                    AccessMappingUnavailable);
            }

            if (!string.Equals(
                    process.ScopeId,
                    tenantId,
                    StringComparison.Ordinal) ||
                process.StaffMemberId != staffMemberId ||
                process.TargetStaffVersion != departureVersion ||
                process.TargetState !=
                    WorkspaceStaffAccessTargetState.Departed ||
                process.State != WorkspaceStaffAccessProcessState.Completed ||
                (subjectId is not null &&
                 !string.Equals(
                     process.SubjectId,
                     subjectId,
                     StringComparison.Ordinal)))
            {
                return WorkspaceStaffAccessClosureResult.Blocked(
                    AccessMappingConflict);
            }

            WorkspaceStaffAccessCoordinationOutcome denied =
                await accessDenier.EnsureAccessDeniedAsync(
                    process.ScopeId,
                    process.SubjectId,
                    WorkspaceStaffAccessTargetState.Departed,
                    cancellationToken).ConfigureAwait(false);
            return denied switch
            {
                WorkspaceStaffAccessCoordinationOutcome.Allowed =>
                    WorkspaceStaffAccessClosureResult.Complete(
                        process.SubjectId),
                WorkspaceStaffAccessCoordinationOutcome.OwnerProtected =>
                    WorkspaceStaffAccessClosureResult.Blocked(
                        OwnerProtected),
                _ => WorkspaceStaffAccessClosureResult.Retry(
                    RetryRequired)
            };
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Workspace Staff anonymisation access closure failed because {ExceptionType} was raised.",
                exception.GetType().Name);
            return WorkspaceStaffAccessClosureResult.Retry(
                RetryRequired);
        }
    }

    private static bool IsValid(
        DataRightsAnonymisationContributionRequestV2? request) =>
        request is not null &&
        request.ContractVersion ==
            DataRightsAnonymisationExecutionPrerequisiteContractV2
                .CurrentVersion &&
        request.CaseType == DataRightsCaseType.StaffRights &&
        request.ScopeKind == DataRightsExecutionScopeKind.Tenant &&
        request.PropertyId is null &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.Coordinate.RecordId != Guid.Empty &&
        request.Coordinate.RecordVersion > 0 &&
        string.Equals(
            request.Coordinate.OwnerKey,
            StaffDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            StringComparison.Ordinal);

    private static bool IsValid(
        DataRightsAnonymisationRestoreRequestV3? request) =>
        request is not null &&
        request.ContractVersion ==
            DataRightsAnonymisationRestoreContractV3.CurrentVersion &&
        request.CaseType == DataRightsCaseType.StaffRights &&
        request.ScopeKind == DataRightsExecutionScopeKind.Tenant &&
        request.RoutingPropertyId is null &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.RecordId != Guid.Empty &&
        request.ResultingRecordVersion > 1 &&
        string.Equals(
            request.OwnerKey,
            StaffDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.RecordType,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            StringComparison.Ordinal);

    private static bool IsValid(
        StaffRetentionAnonymisationPrerequisiteRequest? request) =>
        request is not null &&
        request.ContractVersion ==
            StaffRetentionAnonymisationPrerequisiteContract
                .CurrentVersion &&
        request.ExecutionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.StaffMemberId != Guid.Empty &&
        request.SelectedStaffVersion > 0;

    private static DataRightsAnonymisationExecutionPrerequisiteResult
        ExecutionBlocked(string code) =>
        DataRightsAnonymisationExecutionPrerequisiteResult.Blocked(
            DataRightsAnonymisationExecutionPrerequisiteContractV2
                .CurrentVersion,
            code);

    private static DataRightsAnonymisationRestorePrerequisiteResult
        RestoreBlocked(string code) =>
        DataRightsAnonymisationRestorePrerequisiteResult.Blocked(
            DataRightsAnonymisationRestoreContractV3.CurrentVersion,
            code);

    private static bool IsBlockedScrubFailure(Error error) =>
        error == WorkspaceStaffRetentionErrors.RequestInvalid ||
        error == WorkspaceStaffRetentionErrors.ReceiptInvalid ||
        error == WorkspaceStaffRetentionErrors.ActiveOnboarding ||
        error == WorkspaceStaffRetentionErrors.ActiveAccessProcess ||
        error == WorkspaceStaffRetentionErrors.AccessMappingConflict ||
        error.Code == StateUnavailable ||
        error.Code == StateConflict ||
        error.Code == AccessMappingUnavailable ||
        error.Code == AccessMappingConflict ||
        error.Code == OwnerProtected;

}
