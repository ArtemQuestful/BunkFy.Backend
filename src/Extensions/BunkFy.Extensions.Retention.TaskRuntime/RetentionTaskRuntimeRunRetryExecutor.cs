namespace BunkFy.Extensions.Retention.TaskRuntime;

using System.Text.Json;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Modules.TaskRuntime.Contracts;

internal sealed class RetentionTaskRuntimeRunRetryExecutor(
    ITaskRunReader taskRunReader,
    ITaskRunController taskRunController)
    : IRetentionRunRetryExecutor
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<RetentionRunRetryExecutionOutcome> ExecuteAsync(
        RetentionRunRetryWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        Result<TaskRunDetails> loaded = await taskRunReader.GetAsync(
                workItem.RunId,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return RetentionRunRetryExecutionOutcome.StableFailure(
                "task-run-unavailable");
        }

        TaskRunDetails details = loaded.Value;
        if (!IsOwnedRun(details.Summary, workItem) ||
            !TryReadPayload(details, out ExecuteRetentionSchedulePayload? payload) ||
            !Matches(payload!, workItem))
        {
            return RetentionRunRetryExecutionOutcome.StableFailure(
                "task-run-unavailable");
        }

        string requestToken = CreateRequestToken(workItem.RequestId);
        if (string.Equals(
                details.Summary.RequestedBy,
                requestToken,
                StringComparison.Ordinal))
        {
            return RetentionRunRetryExecutionOutcome.Applied;
        }

        if (details.Summary.Status != TaskRunStatus.Failed)
        {
            return RetentionRunRetryExecutionOutcome.StableFailure(
                "task-run-state-changed");
        }

        Result retried = await taskRunController.RetryAsync(
                workItem.RunId,
                requestToken,
                workItem.ScheduledAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (retried.IsSuccess)
        {
            return RetentionRunRetryExecutionOutcome.Applied;
        }

        return retried.Error == TaskRuntimeOperationErrors.ConcurrentMutation
            ? RetentionRunRetryExecutionOutcome.TransientFailure(
                "task-run-concurrent-mutation")
            : RetentionRunRetryExecutionOutcome.StableFailure(
                MapFailure(retried.Error));
    }

    internal static string CreateRequestToken(Guid requestId) =>
        $"retention-recovery:{requestId:N}";

    private static bool IsOwnedRun(
        TaskRunSummary run,
        RetentionRunRetryWorkItem workItem) =>
        string.Equals(
            run.ScopeId,
            workItem.TenantId,
            StringComparison.Ordinal) &&
        string.Equals(
            run.ModuleName,
            RetentionModuleMetadata.Name,
            StringComparison.Ordinal) &&
        string.Equals(
            run.TaskName,
            ExecuteRetentionSchedulePayload.TaskName,
            StringComparison.Ordinal) &&
        run.PayloadVersion == ExecuteRetentionSchedulePayload.PayloadVersion;

    private static bool TryReadPayload(
        TaskRunDetails details,
        out ExecuteRetentionSchedulePayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<
                ExecuteRetentionSchedulePayload>(
                    details.PayloadJson,
                    SerializerOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }
        catch (NotSupportedException)
        {
            payload = null;
        }

        return payload is not null && IsValid(payload);
    }

    private static bool IsValid(ExecuteRetentionSchedulePayload payload) =>
        IsValidKey(payload.OwnerKey) &&
        IsValidKey(payload.DataClassKey) &&
        payload.ExecutionPolicyVersion > 0 &&
        payload.TargetScopeKind switch
        {
            RetentionTargetScopeKind.Tenant => payload.PropertyId is null,
            RetentionTargetScopeKind.Property =>
                payload.PropertyId is not null &&
                payload.PropertyId != Guid.Empty,
            _ => false
        };

    private static bool IsValidKey(string? value) =>
        value is not null &&
        value.Length is > 0 and <= RetentionExecutionContract.KeyMaxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.') &&
        string.Equals(
            value,
            value.ToLowerInvariant(),
            StringComparison.Ordinal);

    private static bool Matches(
        ExecuteRetentionSchedulePayload payload,
        RetentionRunRetryWorkItem workItem) =>
        string.Equals(
            payload.OwnerKey,
            workItem.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            payload.DataClassKey,
            workItem.DataClassKey,
            StringComparison.Ordinal) &&
        payload.TargetScopeKind == workItem.TargetScopeKind &&
        payload.PropertyId == workItem.PropertyId &&
        payload.ExecutionPolicyVersion == workItem.ExecutionPolicyVersion;

    private static string MapFailure(Error error) =>
        error == TaskRuntimeOperationErrors.ScopeClosed
            ? "task-scope-closed"
            : error == TaskRuntimeOperationErrors.RunNotFound
                ? "task-run-unavailable"
                : "task-run-not-retryable";
}
