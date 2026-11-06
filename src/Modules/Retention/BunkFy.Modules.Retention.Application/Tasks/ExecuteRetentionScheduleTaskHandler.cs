namespace BunkFy.Modules.Retention.Application.Tasks;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Security;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Observability;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Microsoft.Extensions.Logging;

internal sealed class ExecuteRetentionScheduleTaskHandler(
    ITaskCommandDispatcher commandDispatcher,
    IEnumerable<IRetentionExecutionContributor> contributors,
    ISystemClock clock,
    ISecuritySignalRecorder securitySignals,
    ILogger<ExecuteRetentionScheduleTaskHandler> logger)
    : ITaskHandler<ExecuteRetentionSchedulePayload>
{
    public async Task HandleAsync(
        ExecuteRetentionSchedulePayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        string tenantId = context.ScopeId ??
            throw new InvalidOperationException("Retention.ScopeRequired");
        IRetentionExecutionContributor contributor = this.ResolveContributor(payload);
        int executionAttempt = context.LeaseGeneration;
        DateTimeOffset startedAtUtc = clock.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc + contributor.Schedule.ExecutionTimeout;

        Result<RetentionExecutionStart> started =
            await commandDispatcher.DispatchAsync<
                BeginRetentionExecutionCommand,
                RetentionExecutionStart>(
                context,
                new(
                    context.RunId,
                    tenantId,
                    payload.OwnerKey,
                    payload.DataClassKey,
                    payload.TargetScopeKind,
                    payload.PropertyId,
                    payload.ExecutionPolicyVersion,
                    executionAttempt,
                    startedAtUtc,
                    deadlineUtc,
                    startedAtUtc + contributor.Schedule.Interval),
                cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw Failure(started.Error);
        }

        if (!started.Value.DispatchRequired)
        {
            if (started.Value.State == RetentionExecutionState.Failed)
            {
                throw new InvalidOperationException("Retention.PreviousAttemptFailed");
            }

            return;
        }

        RetentionContributionResult result;
        Exception? ownerFailure = null;
        try
        {
            result = await this.ExecuteOwnerAsync(
                contributor,
                started.Value.Request,
                cancellationToken).ConfigureAwait(false);
            result = NormalizeTerminalReplay(
                started.Value,
                result);
            if (!IsValidResult(started.Value.Request, result))
            {
                throw new InvalidOperationException("Retention.OwnerResultInvalid");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Retention owner {OwnerKey} failed for data class {DataClassKey} at attempt {Attempt} because {ExceptionType} was raised",
                payload.OwnerKey,
                payload.DataClassKey,
                executionAttempt,
                exception.GetType().Name);
            ownerFailure = exception;
            result = new(
                RetentionExecutionContract.CurrentVersion,
                RetentionContributionStatus.Failed,
                0,
                0,
                0,
                exception is TimeoutException
                    ? "retention.owner-timeout"
                    : "retention.owner-exception",
                clock.UtcNow);
        }

        Result<Unit> completed =
            await commandDispatcher.DispatchAsync<
                CompleteRetentionExecutionCommand,
                Unit>(
                context,
                new(context.RunId, executionAttempt, result),
                cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            throw Failure(completed.Error);
        }

        if (ownerFailure is not null ||
            result.Status == RetentionContributionStatus.Failed)
        {
            SecuritySignalDefinition signal =
                ownerFailure is TimeoutException ||
                string.Equals(
                    result.OutcomeCode,
                    "retention.owner-timeout",
                    StringComparison.Ordinal)
                    ? RetentionSecuritySignalDefinitions
                        .ScheduledExecutionTimedOut
                    : RetentionSecuritySignalDefinitions
                        .ScheduledExecutionFailed;
            securitySignals.Record(
                signal,
                context.CorrelationId ?? context.RunId);
            throw ownerFailure ??
                new InvalidOperationException("Retention.OwnerReportedFailure");
        }
    }

    private async Task<RetentionContributionResult> ExecuteOwnerAsync(
        IRetentionExecutionContributor contributor,
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = request.DeadlineUtc - clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("Retention.OwnerDeadlineExceeded");
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            return await contributor.ExecuteAsync(
                request,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Retention.OwnerDeadlineExceeded");
        }
    }

    private IRetentionExecutionContributor ResolveContributor(
        ExecuteRetentionSchedulePayload payload)
    {
        IRetentionExecutionContributor[] matches = contributors
            .Where(contributor =>
                string.Equals(
                    contributor.Schedule.OwnerKey,
                    payload.OwnerKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    contributor.Schedule.DataClassKey,
                    payload.DataClassKey,
                    StringComparison.Ordinal) &&
                contributor.Schedule.ExecutionPolicyVersion ==
                    payload.ExecutionPolicyVersion &&
                contributor.Schedule.TargetScopeKind == payload.TargetScopeKind)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "Retention.OwnerContributorUnavailable");
    }

    private static bool IsValidResult(
        RetentionContributionRequest request,
        RetentionContributionResult? result)
    {
        if (result is null ||
            result.ContractVersion != request.ContractVersion ||
            result.Status is RetentionContributionStatus.Unknown ||
            result.ScannedCount < 0 ||
            result.AffectedCount < 0 ||
            result.RemainingCount < 0 ||
            result.AffectedCount > result.ScannedCount ||
            result.CompletedAtUtc < request.StartedAtUtc ||
            result.CompletedAtUtc > request.DeadlineUtc)
        {
            return false;
        }

        string code = result.OutcomeCode?.Trim() ?? string.Empty;
        bool codeValid = code.Length is
            > 0 and <= RetentionExecutionContract.OutcomeCodeMaxLength &&
            code.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '.');
        bool blocked = result.Status == RetentionContributionStatus.Blocked;
        return codeValid &&
            blocked == (result.HoldReviewDueAtUtc is not null);
    }

    private static RetentionContributionResult NormalizeTerminalReplay(
        RetentionExecutionStart start,
        RetentionContributionResult result) =>
        start.AttemptAdvanced &&
        result.CompletedAtUtc != default &&
        result.CompletedAtUtc < start.Request.StartedAtUtc
            ? result with
            {
                CompletedAtUtc = start.Request.StartedAtUtc
            }
            : result;

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");
}
