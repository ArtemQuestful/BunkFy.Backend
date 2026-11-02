namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using Microsoft.EntityFrameworkCore;

internal sealed partial class InventoryTenantTerminationContributor
{
    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        count = await this.ExportInventoryUnitsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportRoomConfigurationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportManagementOperationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportManualBlocksAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportManualBlockGroupsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAllocationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAllocationUnitsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAmendmentDecisionsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAnonymisationReceiptsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAnonymisationTombstonesAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAnonymisationRestoreReceiptsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportBedRetirementsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportRoomRetirementsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportInventoryUnitsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryUnit unit in dbContext.InventoryUnits
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            InventoryUnitTenantExport record = new(
                unit.ScopeId,
                unit.PropertyId,
                unit.RoomId,
                unit.BedId,
                unit.Id,
                unit.Kind,
                unit.Label,
                unit.IsTopologyActive,
                unit.SourceVersion,
                unit.DetailsVersion,
                unit.IsKnown,
                unit.AvailabilityMutationVersion);
            long version = Math.Max(
                unit.AvailabilityMutationVersion,
                Math.Max(unit.SourceVersion, unit.DetailsVersion));
            await WriteAsync(
                InventoryTenantTerminationMetadata.InventoryUnitRecordType,
                unit.Id,
                version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportRoomConfigurationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (RoomInventoryConfiguration configuration in
            dbContext.RoomConfigurations
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryRoomConfigurationTenantExport record = new(
                configuration.ScopeId,
                configuration.PropertyId,
                configuration.Id,
                configuration.SalesMode,
                configuration.Version,
                configuration.AvailabilityMutationVersion,
                configuration.CreatedAtUtc,
                configuration.UpdatedAtUtc);
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .RoomConfigurationRecordType,
                configuration.Id,
                configuration.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportManagementOperationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryManagementOperation operation in
            dbContext.ManagementOperations
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.PropertyId)
                .ThenBy(item => item.ResourceKind)
                .ThenBy(item => item.ResourceId)
                .ThenBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryManagementOperationTenantExport record = new(
                operation.ScopeId,
                operation.PropertyId,
                operation.Id,
                new InventoryManagementOperationStateTenantExport(
                    operation.ResourceKind,
                    operation.ResourceId,
                    operation.Kind,
                    operation.ExpectedVersion,
                    operation.RequestFingerprint,
                    operation.ResultSalesMode,
                    operation.ResultBlockId,
                    operation.ResultBlockGroupId,
                    operation.ResultBlockStatus,
                    operation.ResultAffectedBlockCount,
                    operation.ResultTopologyChangeId,
                    operation.ResultVersion,
                    operation.CompletedAtUtc,
                    operation.ResultBlockGroupStatus,
                    operation.ResultPreviousBlockGroupId,
                    operation.ResultTotalBlockCount,
                    operation.ResultActiveBlockCount,
                    operation.ResultReleasedBlockCount,
                    operation.ResultAlreadyReleasedBlockCount,
                    operation.ResultCreatedBlockCount,
                    operation.ResultMembershipDigest));
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .ManagementOperationRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    operation.PropertyId,
                    $"{(int)operation.ResourceKind}:" +
                    $"{operation.ResourceId:N}:{operation.Id:N}"),
                recordVersion: 1,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportManualBlocksAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (ManualInventoryBlock block in dbContext.ManualBlocks
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            InventoryManualBlockTenantExport record = new(
                block.ScopeId,
                block.Id,
                block.BlockGroupId,
                block.PropertyId,
                block.InventoryUnitId,
                block.Arrival,
                block.Departure,
                block.Reason,
                block.Status,
                block.Version,
                block.CreatedAtUtc,
                block.ReleasedAtUtc);
            await WriteAsync(
                InventoryTenantTerminationMetadata.ManualBlockRecordType,
                block.Id,
                block.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportManualBlockGroupsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (ManualInventoryBlockGroup group in
            dbContext.ManualBlockGroups
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.PropertyId)
                .ThenBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryManualBlockGroupTenantExport record = new(
                group.ScopeId,
                group.Id,
                group.PropertyId,
                new InventoryManualBlockGroupTenantExportState(
                    group.TargetKind,
                    group.BuildingLabel,
                    group.FloorLabel,
                    group.RoomId,
                    group.InventoryUnitId,
                    group.Arrival,
                    group.Departure,
                    group.SelectionDigest,
                    group.MembershipDigest,
                    group.MembershipDigestVersion,
                    group.InitialBlockCount,
                    group.ActiveBlockCount,
                    group.State,
                    group.Version,
                    group.ReplacesGroupId,
                    group.CreatedAtUtc,
                    group.UpdatedAtUtc,
                    group.ReleasedAtUtc),
                group.Reason,
                new InventoryManualBlockGroupActorTenantExport(
                    group.CreatedByActorId,
                    group.LastModifiedByActorId));
            await WriteAsync(
                InventoryTenantTerminationMetadata.ManualBlockGroupRecordType,
                group.Id,
                group.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAllocationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryAllocation allocation in dbContext.Allocations
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            InventoryAllocationTenantExport record = new(
                allocation.ScopeId,
                allocation.PropertyId,
                allocation.ReservationId,
                new InventoryAllocationTenantExportPayload(
                    allocation.Id,
                    allocation.AllocationRequestId,
                    allocation.Arrival,
                    allocation.Departure,
                    allocation.Status,
                    allocation.Rejection,
                    allocation.Version,
                    allocation.ReleaseRequestId,
                    allocation.CreatedAtUtc,
                    allocation.ReleasedAtUtc,
                    allocation.IsAnonymised,
                    allocation.AnonymisedAtUtc));
            await WriteAsync(
                InventoryTenantTerminationMetadata.AllocationRecordType,
                allocation.Id,
                allocation.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAllocationUnitsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        IQueryable<AllocationUnitExportRow> query =
            from unit in dbContext.AllocationUnits.AsNoTracking()
            join allocation in dbContext.Allocations.AsNoTracking()
                on new { unit.ScopeId, unit.AllocationId }
                equals new { allocation.ScopeId, AllocationId = allocation.Id }
            where unit.ScopeId == tenantId
            orderby unit.AllocationId, unit.Id
            select new AllocationUnitExportRow(
                unit.ScopeId,
                unit.AllocationId,
                unit.Id,
                allocation.Version);
        await foreach (AllocationUnitExportRow row in query
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            InventoryAllocationUnitTenantExport record = new(
                row.ScopeId,
                new InventoryAllocationUnitTenantExportPayload(
                    row.AllocationId,
                    row.InventoryUnitId));
            await WriteAsync(
                InventoryTenantTerminationMetadata.AllocationUnitRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.AllocationId,
                    row.InventoryUnitId.ToString("N")),
                row.AllocationVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAmendmentDecisionsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryAllocationAmendmentDecision decision in
            dbContext.AllocationAmendmentDecisions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryAllocationAmendmentDecisionTenantExport record = new(
                decision.ScopeId,
                decision.PropertyId,
                decision.ReservationId,
                new InventoryAllocationAmendmentDecisionTenantExportPayload(
                    decision.Id,
                    decision.AllocationId,
                    decision.RequestFingerprint,
                    decision.Confirmed,
                    decision.RejectionReason,
                    decision.AllocationVersion,
                    decision.DecidedAtUtc));
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .AllocationAmendmentDecisionRecordType,
                decision.Id,
                recordVersion: 1,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAnonymisationReceiptsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryAllocationAnonymisationReceipt receipt in
            dbContext.AllocationAnonymisationReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryAnonymisationReceiptTenantExport record = new(
                receipt.ScopeId,
                new InventoryAnonymisationReceiptProofTenantExport(
                    receipt.ContractVersion,
                    receipt.Id,
                    receipt.WorkItemId,
                    receipt.IdempotencyKey,
                    receipt.PropertyId,
                    receipt.CaseId,
                    receipt.ApprovalRevision,
                    receipt.OperationRevision,
                    receipt.AllocationId,
                    receipt.SelectedAllocationVersion,
                    receipt.ResultingAllocationVersion,
                    receipt.ResultingReservationPseudonym,
                    receipt.Disposition,
                    receipt.Reason,
                    receipt.RemovedAmendmentDecisionCount,
                    receipt.ApprovalEvidenceSha256,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256),
                receipt.ActorId);
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .AllocationAnonymisationReceiptRecordType,
                receipt.Id,
                recordVersion: 1,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAnonymisationTombstonesAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (InventoryAllocationAnonymisationTombstone tombstone in
            dbContext.AllocationAnonymisationTombstones
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryAnonymisationTombstoneTenantExport record = new(
                tombstone.ScopeId,
                new InventoryAnonymisationTombstoneProofTenantExport(
                    tombstone.ContractVersion,
                    tombstone.Revision,
                    tombstone.PropertyId,
                    tombstone.Id,
                    tombstone.OwnerReceiptContractVersion,
                    tombstone.OwnerReceiptId,
                    tombstone.OwnerReceiptSha256,
                    tombstone.ResultingAllocationVersion,
                    tombstone.ResultingReservationPseudonym,
                    tombstone.AllocationPresent,
                    tombstone.CompletedAtUtc,
                    tombstone.LedgerEntryId,
                    tombstone.LastReplayedAtUtc));
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .AllocationAnonymisationTombstoneRecordType,
                tombstone.Id,
                tombstone.Revision,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAnonymisationRestoreReceiptsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (
            InventoryAllocationAnonymisationRestoreReceipt receipt in
            dbContext.AllocationAnonymisationRestoreReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryAnonymisationRestoreReceiptTenantExport record = new(
                receipt.ScopeId,
                new InventoryAnonymisationRestoreReceiptProofTenantExport(
                    receipt.ContractVersion,
                    receipt.LedgerEntryId,
                    receipt.TenantSequence,
                    receipt.LedgerEntrySha256,
                    receipt.PropertyId,
                    receipt.AllocationId,
                    receipt.OwnerReceiptContractVersion,
                    receipt.OwnerReceiptId,
                    receipt.OwnerReceiptSha256,
                    receipt.ResultingAllocationVersion,
                    receipt.ResultingReservationPseudonym,
                    receipt.AllocationPresent,
                    receipt.OriginallyCompletedAtUtc,
                    receipt.TombstoneRevision,
                    receipt.ReplayedAtUtc,
                    receipt.CanonicalSha256));
            await WriteAsync(
                InventoryTenantTerminationMetadata
                    .AllocationAnonymisationRestoreReceiptRecordType,
                receipt.Id,
                recordVersion: 1,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportBedRetirementsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (BedRetirementProcess retirement in
            dbContext.BedRetirements
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryBedRetirementTenantExport record = new(
                retirement.ScopeId,
                retirement.Id,
                retirement.PropertyId,
                retirement.RoomId,
                retirement.BedId,
                retirement.Reason,
                retirement.RequestedBy,
                retirement.CancellationReason,
                retirement.CanceledBy,
                retirement.CanceledAtUtc,
                retirement.State,
                retirement.RejectionReasonCode,
                retirement.Version,
                retirement.CreatedAtUtc,
                retirement.UpdatedAtUtc,
                retirement.CompletedAtUtc);
            await WriteAsync(
                InventoryTenantTerminationMetadata.BedRetirementRecordType,
                retirement.Id,
                retirement.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportRoomRetirementsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (RoomRetirementProcess retirement in
            dbContext.RoomRetirements
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            InventoryRoomRetirementTenantExport record = new(
                retirement.ScopeId,
                retirement.Id,
                retirement.PropertyId,
                retirement.RoomId,
                retirement.Reason,
                retirement.RequestedBy,
                retirement.CancellationReason,
                retirement.CanceledBy,
                retirement.CanceledAtUtc,
                retirement.State,
                retirement.RejectionReasonCode,
                retirement.Version,
                retirement.CreatedAtUtc,
                retirement.UpdatedAtUtc,
                retirement.CompletedAtUtc);
            await WriteAsync(
                InventoryTenantTerminationMetadata.RoomRetirementRecordType,
                retirement.Id,
                retirement.Version,
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
            InventoryTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);

    private sealed record AllocationUnitExportRow(
        string ScopeId,
        Guid AllocationId,
        Guid InventoryUnitId,
        long AllocationVersion);
}
