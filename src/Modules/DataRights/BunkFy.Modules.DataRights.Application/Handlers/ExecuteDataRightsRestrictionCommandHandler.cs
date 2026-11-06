namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;
using RestrictionReleaseTarget = BunkFy.Modules.DataRights.Domain.ValueObjects.DataRightsRestrictionReleaseTarget;
using SelectedSubject = BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class ExecuteDataRightsRestrictionCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IDataRightsOperationApprovalGate approvalGate,
    IEnumerable<IDataRightsRestrictionContributor> contributors,
    ISystemClock clock,
    ILogger<ExecuteDataRightsRestrictionCommandHandler> logger)
    : ICommandHandler<
        ExecuteDataRightsRestrictionCommand,
        DataRightsRestrictionExecutionDto>
{
    private static readonly TimeSpan OwnerDeadline = TimeSpan.FromSeconds(30);

    public async Task<Result<DataRightsRestrictionExecutionDto>> HandleAsync(
        ExecuteDataRightsRestrictionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Scope is null)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionExecutionDenied);
        }

        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        SelectedSubject? subject = dataRightsCase.SelectedSubjects.Count == 1
            ? dataRightsCase.SelectedSubjects.First()
            : null;
        string actor = command.ActorId?.Trim() ?? string.Empty;
        if (command.IdempotencyKey == Guid.Empty ||
            actor.Length is 0 or > DataRightsCase.ActorIdMaxLength ||
            dataRightsCase.Kind != (DataRightsCaseKind)command.Scope.CaseType ||
            dataRightsCase.PropertyId != command.Scope.PropertyId ||
            dataRightsCase.RequestedOperations !=
                DataRightsCaseOperation.Restriction ||
            dataRightsCase.DecisionRevision is not long approvalRevision ||
            dataRightsCase.RestrictionAction is not
                DataRightsRestrictionAction.Apply and not
                DataRightsRestrictionAction.Release ||
            subject is null)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionExecutionDenied);
        }

        if (dataRightsCase.RestrictionExecutionProof is { } existing)
        {
            return existing.Matches(
                command.IdempotencyKey,
                approvalRevision,
                dataRightsCase.RestrictionAction,
                subject,
                actor,
                dataRightsCase.RestrictionReleaseTarget)
                ? Result.Success(new DataRightsRestrictionExecutionDto(
                    dataRightsCase.ToDto(),
                    existing.ToDto()))
                : Result.Failure<DataRightsRestrictionExecutionDto>(
                    DataRightsApplicationErrors.RestrictionExecutionConflict);
        }

        DataRightsRestrictionDirective directive =
            (DataRightsRestrictionDirective)dataRightsCase.RestrictionAction;
        DataRightsOperationApprovalResult approval =
            await approvalGate.EvaluateAsync(
                new DataRightsOperationApprovalRequest(
                    dataRightsCase.ScopeId,
                    command.Scope.PropertyId,
                    command.CaseId,
                    approvalRevision,
                    DataRightsOperation.Restriction,
                    subject.OwnerKey,
                    subject.RecordType,
                    subject.RecordId,
                    subject.RecordVersion,
                    directive,
                    CaseType: command.Scope.CaseType,
                    RestrictionTargetOwnerOperationId:
                        dataRightsCase.RestrictionReleaseTarget?.OwnerOperationId,
                    RestrictionTargetOwnerOperationVersion:
                        dataRightsCase.RestrictionReleaseTarget?.OwnerOperationVersion),
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.IsApproved)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionExecutionDenied);
        }

        Result<IDataRightsRestrictionContributor> resolved =
            DataRightsRestrictionContributorSet.Resolve(
                contributors,
                subject.OwnerKey);
        if (resolved.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                resolved.Error);
        }
        IDataRightsRestrictionContributor contributor = resolved.Value;

        DateTimeOffset nowUtc = clock.UtcNow;
        DateTimeOffset deadlineUtc = nowUtc.Add(OwnerDeadline);
        DataRightsRestrictionContributionRequest ownerRequest = new(
            DataRightsRestrictionContract.CurrentVersion,
            dataRightsCase.ScopeId,
            command.IdempotencyKey,
            command.Scope.PropertyId,
            command.CaseId,
            approvalRevision,
            new BunkFy.Modules.DataRights.Contracts.DataRightsSubjectCoordinate(
                subject.OwnerKey,
                subject.RecordType,
                subject.RecordId,
                subject.RecordVersion),
            directive,
            actor,
            deadlineUtc,
            command.Scope.CaseType,
            dataRightsCase.RestrictionReleaseTarget?.OwnerOperationId,
            dataRightsCase.RestrictionReleaseTarget?.OwnerOperationVersion);
        DataRightsDeadlineExecution<DataRightsRestrictionContributionResult>
            execution;
        try
        {
            execution = await DataRightsDeadlineExecutor.ExecuteAsync(
                clock,
                deadlineUtc,
                "DataRights.RestrictionOwnerDeadlineExceeded",
                token => contributor.ExecuteAsync(ownerRequest, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Data Rights restriction owner {OwnerKey} exceeded its deadline.",
                subject.OwnerKey);
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Data Rights restriction owner {OwnerKey} requires retry because {ExceptionType} was raised.",
                subject.OwnerKey,
                exception.GetType().Name);
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired);
        }
        DataRightsRestrictionContributionResult ownerResult = execution.Value;

        Result<DataRightsRestrictionOwnerProof> validated = Validate(
            ownerResult,
            directive,
            deadlineUtc,
            execution.ObservedAtUtc,
            dataRightsCase.RestrictionReleaseTarget);
        if (validated.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(
                validated.Error);
        }
        DataRightsRestrictionOwnerProof ownerProof = validated.Value;

        Result<DataRightsRestrictionExecutionProof> proof =
            DataRightsRestrictionExecutionProof.Create(
                command.IdempotencyKey,
                approvalRevision,
                dataRightsCase.RestrictionAction,
                subject,
                ownerProof.ReceiptContractVersion,
                ownerProof.ReceiptId,
                ownerProof.OwnerOperationId,
                ownerProof.ResultingOwnerRevision,
                ownerProof.ResultingProjectionRevision,
                ownerProof.EffectiveRestricted,
                ownerProof.ReceiptSha256,
                actor,
                ownerProof.CompletedAtUtc,
                dataRightsCase.RestrictionReleaseTarget);
        if (proof.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionExecutionDto>(proof.Error);
        }

        Result completed = dataRightsCase.CompleteRestrictionExecution(
            command.ExpectedVersion,
            proof.Value,
            clock.UtcNow);
        return completed.IsSuccess
            ? Result.Success(new DataRightsRestrictionExecutionDto(
                dataRightsCase.ToDto(),
                proof.Value.ToDto()))
            : Result.Failure<DataRightsRestrictionExecutionDto>(completed.Error);
    }

    private static Result<DataRightsRestrictionOwnerProof> Validate(
        DataRightsRestrictionContributionResult? result,
        DataRightsRestrictionDirective directive,
        DateTimeOffset deadlineUtc,
        DateTimeOffset observedAtUtc,
        RestrictionReleaseTarget? releaseTarget)
    {
        if (result is null ||
            result.ContractVersion != DataRightsRestrictionContract.CurrentVersion)
        {
            return InvalidOwnerResult();
        }

        if (result.Status == DataRightsRestrictionContributionStatus.Blocked)
        {
            return result.OwnerProof is null && IsStableOutcomeCode(result.OutcomeCode)
                ? Result.Failure<DataRightsRestrictionOwnerProof>(
                    DataRightsApplicationErrors.RestrictionExecutionBlocked)
                : InvalidOwnerResult();
        }

        if (result.Status == DataRightsRestrictionContributionStatus.Failed)
        {
            return result.OwnerProof is null && IsStableOutcomeCode(result.OutcomeCode)
                ? Result.Failure<DataRightsRestrictionOwnerProof>(
                    DataRightsApplicationErrors.RestrictionOwnerRetryRequired)
                : InvalidOwnerResult();
        }

        DataRightsRestrictionOwnerProof? proof = result.OwnerProof;
        bool effectiveStateValid = directive switch
        {
            DataRightsRestrictionDirective.Apply =>
                releaseTarget is null && proof?.EffectiveRestricted == true,
            DataRightsRestrictionDirective.Release when releaseTarget is null =>
                proof?.EffectiveRestricted == false,
            DataRightsRestrictionDirective.Release =>
                proof is not null &&
                proof.OwnerOperationId == releaseTarget.OwnerOperationId &&
                proof.ResultingOwnerRevision ==
                    releaseTarget.OwnerOperationVersion + 1,
            _ => false
        };
        return result.Status == DataRightsRestrictionContributionStatus.Completed &&
            result.OutcomeCode is null &&
            proof is not null &&
            proof.ReceiptContractVersion > 0 &&
            proof.ReceiptId != Guid.Empty &&
            proof.OwnerOperationId != Guid.Empty &&
            proof.ResultingOwnerRevision > 0 &&
            proof.ResultingProjectionRevision > 0 &&
            effectiveStateValid &&
            proof.ReceiptSha256 is not null &&
            proof.ReceiptSha256.Length == DataRightsRestrictionContract.Sha256Length &&
            proof.ReceiptSha256.All(Uri.IsHexDigit) &&
            proof.CompletedAtUtc != default &&
            proof.CompletedAtUtc <= observedAtUtc &&
            proof.CompletedAtUtc < deadlineUtc
            ? Result.Success(proof)
            : InvalidOwnerResult();
    }

    private static bool IsStableOutcomeCode(string? value) =>
        value is { Length: > 0 and <= DataRightsRestrictionContract.CodeMaxLength } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_');

    private static Result<DataRightsRestrictionOwnerProof> InvalidOwnerResult() =>
        Result.Failure<DataRightsRestrictionOwnerProof>(
            DataRightsApplicationErrors.RestrictionOwnerProofInvalid);
}
