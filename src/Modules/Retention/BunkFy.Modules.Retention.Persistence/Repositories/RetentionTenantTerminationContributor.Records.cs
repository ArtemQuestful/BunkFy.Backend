namespace BunkFy.Modules.Retention.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Models;
using Microsoft.EntityFrameworkCore;

internal sealed partial class RetentionTenantTerminationContributor
{
    private static readonly Guid ScheduleStateRecordNamespaceId =
        Guid.Parse(
            RetentionTenantTerminationMetadata
                .ScheduleStateRecordNamespaceId);

    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = await this.ExportExecutionsAsync(
            tenantId,
            sink,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportScheduleStatesAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportRunRetryRequestsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportExecutionsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (ExecutionExportRow row in dbContext.Executions
            .AsNoTracking()
            .Where(execution => execution.ScopeId == tenantId)
            .OrderBy(execution => execution.Id)
            .Select(execution => new ExecutionExportRow(
                execution.ScopeId,
                execution.Id,
                execution.OwnerKey,
                execution.DataClassKey,
                execution.TargetKind,
                execution.PropertyId,
                execution.ExecutionPolicyVersion,
                execution.Attempt,
                execution.State,
                execution.StartedAtUtc,
                execution.DeadlineUtc,
                execution.CompletedAtUtc,
                execution.ScannedCount,
                execution.AffectedCount,
                execution.RemainingCount,
                execution.OutcomeCode,
                execution.HoldReviewDueAtUtc,
                execution.Version))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            RetentionExecutionTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                new(
                    row.ExecutionId,
                    row.OwnerKey,
                    row.DataClassKey,
                    row.TargetKind,
                    row.ExecutionPolicyVersion,
                    row.Attempt,
                    row.State,
                    row.StartedAtUtc,
                    row.DeadlineUtc,
                    row.CompletedAtUtc,
                    row.ScannedCount,
                    row.AffectedCount,
                    row.RemainingCount,
                    row.OutcomeCode,
                    row.HoldReviewDueAtUtc,
                    row.Version));
            await WriteAsync(
                RetentionTenantTerminationMetadata.ExecutionRecordType,
                row.ExecutionId,
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportScheduleStatesAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (ScheduleStateExportRow row in dbContext.ScheduleStates
            .AsNoTracking()
            .Where(state => state.ScopeId == tenantId)
            .OrderBy(state => state.OwnerKey)
            .ThenBy(state => state.DataClassKey)
            .ThenBy(state => state.TargetKey)
            .ThenBy(state => state.ExecutionPolicyVersion)
            .Select(state => new ScheduleStateExportRow(
                state.ScopeId,
                state.OwnerKey,
                state.DataClassKey,
                state.TargetKey,
                state.PropertyId,
                state.ExecutionPolicyVersion,
                state.LastExecutionId,
                state.State,
                state.LastStartedAtUtc,
                state.LastCompletedAtUtc,
                state.NextDueAtUtc,
                state.ConsecutiveFailures,
                state.LastScannedCount,
                state.LastAffectedCount,
                state.LastRemainingCount,
                state.OutcomeCode,
                state.HoldReviewDueAtUtc,
                state.Version))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            RetentionScheduleStateTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                new(
                    row.OwnerKey,
                    row.DataClassKey,
                    row.TargetKey,
                    row.ExecutionPolicyVersion,
                    row.LastExecutionId,
                    row.State,
                    row.LastStartedAtUtc,
                    row.LastCompletedAtUtc,
                    row.NextDueAtUtc,
                    row.ConsecutiveFailures,
                    row.LastScannedCount,
                    row.LastAffectedCount,
                    row.LastRemainingCount,
                    row.OutcomeCode,
                    row.HoldReviewDueAtUtc,
                    row.Version));
            await WriteAsync(
                RetentionTenantTerminationMetadata.ScheduleStateRecordType,
                CreateScheduleStateRecordId(row),
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static Guid CreateScheduleStateRecordId(
        ScheduleStateExportRow row) =>
        DataRightsExportRecordIds.CreateDeterministicChild(
            ScheduleStateRecordNamespaceId,
            string.Join(
                '|',
                row.ScopeId,
                row.OwnerKey,
                row.DataClassKey,
                row.TargetKey,
                row.ExecutionPolicyVersion.ToString(
                    CultureInfo.InvariantCulture)));

    private async Task<long> ExportRunRetryRequestsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (RunRetryRequestExportRow row in dbContext
            .RunRetryRequests
            .AsNoTracking()
            .Where(request => request.ScopeId == tenantId)
            .OrderBy(request => request.Id)
            .Select(request => new RunRetryRequestExportRow(
                request.ScopeId,
                request.Id,
                request.RunId,
                request.OwnerKey,
                request.DataClassKey,
                request.TargetKind,
                request.PropertyId,
                request.ExecutionPolicyVersion,
                request.EvidenceVersion,
                request.Attempt,
                request.State,
                request.RequestedAtUtc,
                request.ScheduledAtUtc,
                request.CompletedAtUtc,
                request.FailureCode,
                request.Version))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            RetentionRunRetryRequestTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                new(
                    row.RequestId,
                    row.RunId,
                    row.OwnerKey,
                    row.DataClassKey,
                    row.TargetKind,
                    row.ExecutionPolicyVersion,
                    row.EvidenceVersion,
                    row.Attempt,
                    row.State,
                    row.RequestedAtUtc,
                    row.ScheduledAtUtc,
                    row.CompletedAtUtc,
                    row.FailureCode,
                    row.Version));
            await WriteAsync(
                RetentionTenantTerminationMetadata.RunRetryRequestRecordType,
                row.RequestId,
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        sink.WriteAsync(
            RetentionTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);

    private sealed record ExecutionExportRow(
        string ScopeId,
        Guid ExecutionId,
        string OwnerKey,
        string DataClassKey,
        RetentionExecutionTargetKind TargetKind,
        Guid? PropertyId,
        int ExecutionPolicyVersion,
        int Attempt,
        RetentionExecutionState State,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset DeadlineUtc,
        DateTimeOffset? CompletedAtUtc,
        int? ScannedCount,
        int? AffectedCount,
        int? RemainingCount,
        string? OutcomeCode,
        DateTimeOffset? HoldReviewDueAtUtc,
        long Version);

    private sealed record ScheduleStateExportRow(
        string ScopeId,
        string OwnerKey,
        string DataClassKey,
        string TargetKey,
        Guid? PropertyId,
        int ExecutionPolicyVersion,
        Guid LastExecutionId,
        RetentionExecutionState State,
        DateTimeOffset LastStartedAtUtc,
        DateTimeOffset? LastCompletedAtUtc,
        DateTimeOffset NextDueAtUtc,
        int ConsecutiveFailures,
        int? LastScannedCount,
        int? LastAffectedCount,
        int? LastRemainingCount,
        string? OutcomeCode,
        DateTimeOffset? HoldReviewDueAtUtc,
        long Version);

    private sealed record RunRetryRequestExportRow(
        string ScopeId,
        Guid RequestId,
        Guid RunId,
        string OwnerKey,
        string DataClassKey,
        RetentionExecutionTargetKind TargetKind,
        Guid? PropertyId,
        int ExecutionPolicyVersion,
        long EvidenceVersion,
        int Attempt,
        RetentionRunRetryRequestState State,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? ScheduledAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? FailureCode,
        long Version);
}
