namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Retention;
using Microsoft.EntityFrameworkCore;

internal sealed partial class GuestsTenantTerminationContributor
{
    private async Task<long> ExportRetentionRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        count = await this.ExportRetentionExecutionsAsync(
            tenantId, sink, count, cancellationToken).ConfigureAwait(false);
        return await this.ExportRetentionReceiptsAsync(
            tenantId, sink, count, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportRetentionExecutionsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (GuestRetentionExecution execution in
            dbContext.RetentionExecutions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            GuestRetentionExecutionTenantExport record = new(
                execution.ScopeId,
                execution.Id,
                new GuestRetentionExecutionStateTenantExport(
                    execution.DataClassKey,
                    execution.ExecutionPolicyVersion,
                    execution.Attempt,
                    execution.StartingProjectionOrdinal,
                    execution.State,
                    execution.StartedAtUtc,
                    execution.DeadlineUtc,
                    execution.CompletedAtUtc,
                    execution.AffectedCount,
                    execution.ScannedCount,
                    execution.RemainingCount,
                    execution.OutcomeCode,
                    execution.HoldReviewDueAtUtc,
                    execution.Version));
            await WriteAsync(
                GuestsTenantTerminationMetadata.RetentionExecutionRecordType,
                execution.Id,
                execution.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportRetentionReceiptsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (GuestRetentionAnonymisationReceipt receipt in
            dbContext.RetentionAnonymisationReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            GuestRetentionAnonymisationReceiptTenantExport record = new(
                receipt.ScopeId,
                receipt.GuestId,
                receipt.Id,
                new GuestRetentionAnonymisationProofTenantExport(
                    receipt.ContractVersion,
                    receipt.ExecutionId,
                    receipt.SelectedGuestVersion,
                    receipt.ResultingGuestVersion,
                    receipt.AffectedPropertyCount,
                    receipt.RetentionDeadlineUtc,
                    receipt.PolicySetSha256,
                    receipt.TimeZoneCatalogVersion,
                    receipt.EventId,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256),
                new GuestActorStaffTenantExport(receipt.ActorId));
            await WriteAsync(
                GuestsTenantTerminationMetadata
                    .RetentionAnonymisationReceiptRecordType,
                receipt.Id,
                receipt.ResultingGuestVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }
}
