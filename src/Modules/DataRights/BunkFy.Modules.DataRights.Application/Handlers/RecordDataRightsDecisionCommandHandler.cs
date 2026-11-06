namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class RecordDataRightsDecisionCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IDataRightsAnonymisationApprovalPolicy anonymisationPolicy,
    ISystemClock clock) : ICommandHandler<RecordDataRightsDecisionCommand, DataRightsCaseDto>
{
    public async Task<Result<DataRightsCaseDto>> HandleAsync(
        RecordDataRightsDecisionCommand command,
        CancellationToken cancellationToken)
    {
        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        DataRightsApprovalPolicyEvidence? approvalEvidence = null;
        bool approvesAnonymisation =
            command.Decision == DataRightsDecisionOutcome.Approved &&
            (dataRightsCase.RequestedOperations & DataRightsCaseOperation.Anonymisation) != 0;
        if (approvesAnonymisation)
        {
            if (dataRightsCase.RequestedOperations != DataRightsCaseOperation.Anonymisation)
            {
                return Result.Failure<DataRightsCaseDto>(
                    DataRightsApplicationErrors.AnonymisationMustBeApprovedSeparately);
            }

            Result<DataRightsApprovalPolicyEvidence> policy =
                await anonymisationPolicy.EvaluateAsync(
                    dataRightsCase.ScopeId,
                    command.Scope,
                    command.CaseId,
                    dataRightsCase.SelectedSubjects,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (policy.IsFailure)
            {
                return Result.Failure<DataRightsCaseDto>(policy.Error);
            }

            approvalEvidence = policy.Value;
        }

        Result result = dataRightsCase.RecordDecision(
            (DataRightsCaseDecision)command.Decision,
            (DataRightsCaseDecisionReason)command.Reason,
            command.ExpectedVersion,
            command.ActorId,
            clock.UtcNow,
            approvalEvidence);
        return result.IsSuccess
            ? Result.Success(dataRightsCase.ToDto())
            : Result.Failure<DataRightsCaseDto>(result.Error);
    }
}
