namespace BunkFy.Modules.DataRights.Application.Tasks;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;

internal sealed class DataRightsAnonymisationTaskExecutor(
    ITaskCommandDispatcher commandDispatcher,
    IEnumerable<IDataRightsAnonymisationContributor> propertyContributors,
    IEnumerable<IDataRightsAnonymisationContributorV2> scopedContributors,
    ISystemClock clock,
    IEnumerable<IDataRightsAnonymisationExecutionPrerequisiteV2>?
        scopedPrerequisites = null)
{
    public async Task ExecuteAsync(
        Guid workItemId,
        Guid caseId,
        DataRightsCaseScope scope,
        long approvalRevision,
        long executionRevision,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        Result<DataRightsAnonymisationWorkItemStart> started =
            await commandDispatcher.DispatchAsync<
                BeginDataRightsAnonymisationWorkItemCommand,
                DataRightsAnonymisationWorkItemStart>(
                context,
                new BeginDataRightsAnonymisationWorkItemCommand(
                    workItemId,
                    caseId,
                    scope,
                    approvalRevision,
                    executionRevision,
                    context.RunId,
                    context.Attempt),
                cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (started.Value.DispatchRequired)
        {
            DataRightsAnonymisationContributionResult result =
                await this.DispatchOwnerAsync(
                    started.Value,
                    cancellationToken).ConfigureAwait(false);
            Result<Unit> recorded =
                await commandDispatcher.DispatchAsync<
                    RecordDataRightsAnonymisationOwnerResultCommand,
                    Unit>(
                    context,
                    new RecordDataRightsAnonymisationOwnerResultCommand(
                        workItemId,
                        caseId,
                        scope,
                        approvalRevision,
                        executionRevision,
                        context.RunId,
                        context.Attempt,
                        started.Value.WorkItemVersion,
                        result),
                    cancellationToken).ConfigureAwait(false);
            if (recorded.IsFailure)
            {
                throw Failure(recorded.Error);
            }
        }

        Result<Unit> finalized =
            await commandDispatcher.DispatchAsync<
                FinalizeDataRightsAnonymisationLedgerCommand,
                Unit>(
                context,
                new FinalizeDataRightsAnonymisationLedgerCommand(
                    workItemId,
                    caseId,
                    scope,
                    approvalRevision,
                    executionRevision,
                    context.RunId),
                cancellationToken).ConfigureAwait(false);
        if (finalized.IsFailure)
        {
            throw Failure(finalized.Error);
        }
    }

    private async Task<DataRightsAnonymisationContributionResult>
        DispatchOwnerAsync(
            DataRightsAnonymisationWorkItemStart started,
            CancellationToken cancellationToken)
    {
        if (started.PropertyRequest is { } propertyRequest &&
            started.ScopedRequest is null)
        {
            IDataRightsAnonymisationContributor contributor =
                this.ResolvePropertyContributor(propertyRequest);
            return await this.ExecuteWithDeadlineAsync(
                propertyRequest.ContractVersion,
                propertyRequest.Coordinate.RecordVersion,
                propertyRequest.DeadlineUtc,
                token => contributor.ExecuteAsync(propertyRequest, token),
                cancellationToken).ConfigureAwait(false);
        }

        if (started.ScopedRequest is { } scopedRequest &&
            started.PropertyRequest is null)
        {
            DataRightsAnonymisationContributionResult? blocked =
                await this.ExecuteScopedPrerequisiteAsync(
                    scopedRequest,
                    cancellationToken).ConfigureAwait(false);
            if (blocked is not null)
            {
                return blocked;
            }

            IDataRightsAnonymisationContributorV2 contributor =
                this.ResolveScopedContributor(scopedRequest);
            return await this.ExecuteWithDeadlineAsync(
                scopedRequest.ContractVersion,
                scopedRequest.Coordinate.RecordVersion,
                scopedRequest.DeadlineUtc,
                token => contributor.ExecuteAsync(scopedRequest, token),
                cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "DataRights.AnonymisationRequestUnavailable");
    }

    private async Task<DataRightsAnonymisationContributionResult?>
        ExecuteScopedPrerequisiteAsync(
            DataRightsAnonymisationContributionRequestV2 request,
            CancellationToken cancellationToken)
    {
        if (request.CaseType != DataRightsCaseType.StaffRights)
        {
            return null;
        }

        IDataRightsAnonymisationExecutionPrerequisiteV2 prerequisite =
            this.ResolveScopedPrerequisite(request);
        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await this.ExecutePrerequisiteWithDeadlineAsync(
                request.ContractVersion,
                request.DeadlineUtc,
                token => prerequisite.ExecuteAsync(request, token),
                cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            DataRightsAnonymisationExecutionPrerequisiteStatus.Completed =>
                null,
            DataRightsAnonymisationExecutionPrerequisiteStatus.Blocked =>
                DataRightsAnonymisationContributionResult.Blocked(
                    request.ContractVersion,
                    result.OutcomeCode!),
            DataRightsAnonymisationExecutionPrerequisiteStatus.RetryRequired =>
                throw new InvalidOperationException(
                    "DataRights.AnonymisationPrerequisiteRetryRequired: " +
                    result.OutcomeCode),
            _ => throw new InvalidOperationException(
                "DataRights.AnonymisationPrerequisiteResultInvalid")
        };
    }

    private IDataRightsAnonymisationContributor ResolvePropertyContributor(
        DataRightsAnonymisationContributionRequest request)
    {
        IDataRightsAnonymisationContributor[] matches = propertyContributors
            .Where(contributor =>
                string.Equals(
                    contributor.OwnerKey,
                    request.Coordinate.OwnerKey,
                    StringComparison.Ordinal) &&
                contributor.ContractVersion == request.ContractVersion)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "DataRights.AnonymisationOwnerContributorUnavailable");
    }

    private IDataRightsAnonymisationContributorV2 ResolveScopedContributor(
        DataRightsAnonymisationContributionRequestV2 request)
    {
        IDataRightsAnonymisationContributorV2[] matches = scopedContributors
            .Where(contributor =>
                contributor.CaseType == request.CaseType &&
                string.Equals(
                    contributor.OwnerKey,
                    request.Coordinate.OwnerKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    contributor.RecordType,
                    request.Coordinate.RecordType,
                    StringComparison.Ordinal) &&
                contributor.ContractVersion == request.ContractVersion)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "DataRights.AnonymisationOwnerContributorUnavailable");
    }

    private IDataRightsAnonymisationExecutionPrerequisiteV2
        ResolveScopedPrerequisite(
            DataRightsAnonymisationContributionRequestV2 request)
    {
        IDataRightsAnonymisationExecutionPrerequisiteV2[] matches =
            (scopedPrerequisites ?? [])
            .Where(prerequisite =>
                prerequisite.CaseType == request.CaseType &&
                string.Equals(
                    prerequisite.OwnerKey,
                    request.Coordinate.OwnerKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    prerequisite.RecordType,
                    request.Coordinate.RecordType,
                    StringComparison.Ordinal) &&
                prerequisite.ContractVersion == request.ContractVersion)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "DataRights.AnonymisationPrerequisiteUnavailable");
    }

    private async Task<DataRightsAnonymisationExecutionPrerequisiteResult>
        ExecutePrerequisiteWithDeadlineAsync(
            int contractVersion,
            DateTimeOffset deadlineUtc,
            Func<
                CancellationToken,
                Task<DataRightsAnonymisationExecutionPrerequisiteResult>>
                execute,
            CancellationToken cancellationToken)
    {
        DataRightsDeadlineExecution<
            DataRightsAnonymisationExecutionPrerequisiteResult> execution =
            await DataRightsDeadlineExecutor.ExecuteAsync(
                clock,
                deadlineUtc,
                "DataRights.AnonymisationPrerequisiteDeadlineExceeded",
                execute,
                cancellationToken).ConfigureAwait(false);
        DataRightsAnonymisationExecutionPrerequisiteResult result =
            execution.Value;

        if (!IsValidPrerequisiteResult(contractVersion, result))
        {
            throw new InvalidOperationException(
                "DataRights.AnonymisationPrerequisiteResultInvalid");
        }

        return result;
    }

    private async Task<DataRightsAnonymisationContributionResult>
        ExecuteWithDeadlineAsync(
            int contractVersion,
            long selectedRecordVersion,
            DateTimeOffset deadlineUtc,
            Func<CancellationToken, Task<DataRightsAnonymisationContributionResult>>
                execute,
            CancellationToken cancellationToken)
    {
        DataRightsDeadlineExecution<DataRightsAnonymisationContributionResult>
            execution = await DataRightsDeadlineExecutor.ExecuteAsync(
                clock,
                deadlineUtc,
                "DataRights.AnonymisationOwnerDeadlineExceeded",
                execute,
                cancellationToken).ConfigureAwait(false);
        DataRightsAnonymisationContributionResult result = execution.Value;

        if (!IsValidResult(
                contractVersion,
                selectedRecordVersion,
                deadlineUtc,
                execution.ObservedAtUtc,
                result))
        {
            throw new InvalidOperationException(
                "DataRights.AnonymisationOwnerResultInvalid");
        }

        return result;
    }

    private static bool IsValidResult(
        int contractVersion,
        long selectedRecordVersion,
        DateTimeOffset deadlineUtc,
        DateTimeOffset observedAtUtc,
        DataRightsAnonymisationContributionResult? result)
    {
        if (result is null || result.ContractVersion != contractVersion)
        {
            return false;
        }

        if (result.Status ==
            DataRightsAnonymisationContributionStatus.Completed)
        {
            DataRightsAnonymisationOwnerProof? proof = result.OwnerProof;
            return result.OutcomeCode is null &&
                proof is not null &&
                proof.ReceiptContractVersion > 0 &&
                proof.ReceiptId != Guid.Empty &&
                proof.ResultingRecordVersion > selectedRecordVersion &&
                IsCode(
                    proof.DispositionCode,
                    DataRightsExecutionWorkItem.OwnerCodeMaxLength) &&
                IsCode(
                    proof.ReasonCode,
                    DataRightsExecutionWorkItem.OwnerCodeMaxLength) &&
                IsSha256(proof.ReceiptSha256) &&
                proof.CompletedAtUtc != default &&
                proof.CompletedAtUtc <= observedAtUtc &&
                proof.CompletedAtUtc < deadlineUtc;
        }

        return result.Status is
                DataRightsAnonymisationContributionStatus.Blocked or
                DataRightsAnonymisationContributionStatus.Failed &&
            result.OwnerProof is null &&
            IsCode(
                result.OutcomeCode,
                DataRightsAnonymisationContract.CodeMaxLength);
    }

    private static bool IsValidPrerequisiteResult(
        int contractVersion,
        DataRightsAnonymisationExecutionPrerequisiteResult? result)
    {
        if (result is null || result.ContractVersion != contractVersion)
        {
            return false;
        }

        return result.Status switch
        {
            DataRightsAnonymisationExecutionPrerequisiteStatus.Completed =>
                result.OutcomeCode is null,
            DataRightsAnonymisationExecutionPrerequisiteStatus.Blocked or
                DataRightsAnonymisationExecutionPrerequisiteStatus
                    .RetryRequired =>
                IsCode(
                    result.OutcomeCode,
                    DataRightsAnonymisationContract.CodeMaxLength),
            _ => false
        };
    }

    private static bool IsCode(string? value, int maxLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maxLength;
    }

    private static bool IsSha256(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length ==
                DataRightsAnonymisationContract.Sha256Length &&
            normalized.All(Uri.IsHexDigit);
    }

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
