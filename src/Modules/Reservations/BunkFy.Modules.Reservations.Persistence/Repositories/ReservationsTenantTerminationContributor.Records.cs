namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using Microsoft.EntityFrameworkCore;

internal sealed partial class ReservationsTenantTerminationContributor
{
    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        count = await this.ExportReservationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportRequestedInventoryUnitsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportPendingAmendmentsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportGuestLinksAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportGuestRecordLinkProcessesAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportHistoryRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportGovernanceRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportRetentionRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportReservationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (Reservation reservation in dbContext.Reservations
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ReservationTenantExport record = new(
                reservation.ScopeId,
                reservation.PropertyId,
                reservation.Id,
                new ReservationBookingStateTenantExport(
                    reservation.AllocationRequestId,
                    reservation.AllocationId,
                    reservation.AllocationVersion,
                    reservation.AllocationRejection,
                    reservation.ReleaseRequestId,
                    reservation.LastReleaseRejectionCode,
                    reservation.PendingAllocationAmendmentId,
                    reservation.PendingInventoryAmendmentRequestId,
                    reservation.LastAllocationAmendmentRejectionCode,
                    reservation.PendingStayBusinessDate,
                    reservation.CheckedInBusinessDate,
                    reservation.CheckedInAtUtc,
                    reservation.NoShowBusinessDate,
                    reservation.NoShowAtUtc,
                    reservation.CheckedOutBusinessDate,
                    reservation.CheckedOutAtUtc,
                    reservation.Arrival,
                    reservation.Departure,
                    reservation.ExpectedArrivalTime,
                    reservation.ExpectedDepartureTime,
                    reservation.DetailsRevision,
                    reservation.Status,
                    reservation.Version,
                    reservation.CreatedAtUtc,
                    reservation.UpdatedAtUtc,
                    reservation.TerminalAtUtc,
                    reservation.IsAnonymised,
                    reservation.AnonymisedAtUtc),
                new ReservationGuestDetailsTenantExport(
                    reservation.PrimaryGuestName,
                    reservation.Email,
                    reservation.Phone,
                    reservation.GuestCount,
                    reservation.Notes),
                new ReservationProviderProvenanceTenantExport(
                    reservation.Source,
                    reservation.SourceSystem,
                    reservation.SourceReference,
                    reservation.LastDetailsChangeOrigin,
                    reservation.LastDetailsAdapterConnectionId,
                    reservation.LastDetailsExternalOperationId,
                    reservation.LastDetailsChangedAtUtc),
                new ReservationStaffAttributionTenantExport(
                    reservation.PendingCancellationActorId,
                    reservation.PendingStayActorId,
                    reservation.CheckedInBy,
                    reservation.NoShowBy,
                    reservation.CheckedOutBy,
                    reservation.LastDetailsActorId));
            await WriteAsync(
                ReservationsTenantTerminationMetadata.ReservationRecordType,
                reservation.Id,
                reservation.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportRequestedInventoryUnitsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        IQueryable<RequestedInventoryUnitExportRow> query =
            from unit in dbContext.RequestedInventoryUnits.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on new { unit.ScopeId, unit.ReservationId }
                equals new
                {
                    reservation.ScopeId,
                    ReservationId = reservation.Id
                }
            where unit.ScopeId == tenantId
            orderby unit.ReservationId, unit.Id
            select new RequestedInventoryUnitExportRow(
                unit.ScopeId,
                reservation.PropertyId,
                unit.ReservationId,
                unit.Id,
                reservation.Version);
        await foreach (RequestedInventoryUnitExportRow row in query
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ReservationRequestedInventoryUnitTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.ReservationId,
                row.InventoryUnitId);
            await WriteAsync(
                ReservationsTenantTerminationMetadata
                    .RequestedInventoryUnitRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.ReservationId,
                    row.InventoryUnitId.ToString("N")),
                row.ReservationVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportPendingAmendmentsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (Reservation reservation in dbContext.Reservations
            .AsNoTracking()
            .Where(item =>
                item.ScopeId == tenantId &&
                item.PendingAllocationAmendmentId != null)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ReservationPendingAmendmentTenantExport record = new(
                reservation.ScopeId,
                reservation.PropertyId,
                reservation.Id,
                reservation.PendingAllocationAmendmentId!.Value,
                new ReservationPendingAmendmentStateTenantExport(
                    reservation.PendingArrival!.Value,
                    reservation.PendingDeparture!.Value,
                    reservation.PendingExpectedArrivalTime,
                    reservation.PendingExpectedDepartureTime,
                    ParseInventoryUnitIds(
                        reservation.PendingInventoryUnitIds),
                    reservation.Version),
                new ReservationGuestDetailsTenantExport(
                    reservation.PendingPrimaryGuestName!,
                    reservation.PendingEmail,
                    reservation.PendingPhone,
                    reservation.PendingGuestCount!.Value,
                    reservation.PendingNotes),
                new ReservationPendingAmendmentProviderTenantExport(
                    reservation.PendingAllocationAmendmentRequestFingerprint!,
                    reservation.PendingDetailsChangeOrigin,
                    reservation.PendingDetailsAdapterConnectionId,
                    reservation.PendingDetailsExternalOperationId,
                    reservation.PendingDetailsCorrelationId!.Value),
                new ReservationPendingAmendmentStaffTenantExport(
                    reservation.PendingDetailsActorId));
            await WriteAsync(
                ReservationsTenantTerminationMetadata
                    .PendingAmendmentRecordType,
                reservation.PendingAllocationAmendmentId.Value,
                reservation.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportGuestLinksAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        IQueryable<GuestLinkExportRow> query =
            from link in dbContext.ReservationGuests.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on new { link.ScopeId, link.ReservationId }
                equals new
                {
                    reservation.ScopeId,
                    ReservationId = reservation.Id
                }
            where link.ScopeId == tenantId
            orderby link.ReservationId, link.Id
            select new GuestLinkExportRow(
                link.ScopeId,
                reservation.PropertyId,
                link.ReservationId,
                link.Id,
                link.Role,
                link.LinkedAtUtc,
                link.LinkVersion,
                link.IsCurrent,
                link.UnlinkedAtUtc,
                link.UnlinkedArrival,
                link.UnlinkedDeparture,
                link.UnlinkedReservationStatus,
                link.UnlinkedCheckedInBusinessDate,
                link.UnlinkedNoShowBusinessDate,
                link.UnlinkedCheckedOutBusinessDate,
                link.LinkedBy,
                link.UnlinkedBy);
        await foreach (GuestLinkExportRow row in query
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ReservationGuestLinkTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.ReservationId,
                row.GuestId,
                new ReservationGuestLinkStateTenantExport(
                    row.Role,
                    row.LinkedAtUtc,
                    row.LinkVersion,
                    row.IsCurrent,
                    row.UnlinkedAtUtc,
                    row.UnlinkedArrival,
                    row.UnlinkedDeparture,
                    row.UnlinkedReservationStatus,
                    row.UnlinkedCheckedInBusinessDate,
                    row.UnlinkedNoShowBusinessDate,
                    row.UnlinkedCheckedOutBusinessDate),
                new ReservationGuestLinkStaffTenantExport(
                    row.LinkedBy,
                    row.UnlinkedBy));
            await WriteAsync(
                ReservationsTenantTerminationMetadata.GuestLinkRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.ReservationId,
                    row.GuestId.ToString("N")),
                row.LinkVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportGuestRecordLinkProcessesAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (ReservationGuestRecordLinkProcess process in dbContext
            .GuestRecordLinkProcesses
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ReservationGuestRecordLinkProcessTenantExport record = new(
                process.ScopeId,
                process.PropertyId,
                process.ReservationId,
                process.Id,
                process.Id,
                new(
                    process.CreationConfirmationId,
                    process.ExpectedReservationVersion,
                    process.State,
                    process.ReviewReason,
                    process.Revision,
                    process.DispatchRevision,
                    process.CreatedAtUtc,
                    process.UpdatedAtUtc),
                new(process.RequestedBy));
            await WriteAsync(
                ReservationsTenantTerminationMetadata
                    .GuestRecordLinkProcessRecordType,
                process.Id,
                process.Revision,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static Guid[] ParseInventoryUnitIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "The pending reservation amendment inventory is unavailable.");
        }

        return value.Split(',')
            .Select(item => Guid.ParseExact(item, "N"))
            .Order()
            .ToArray();
    }

    private static ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        sink.WriteAsync(
            ReservationsTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);

    private sealed record RequestedInventoryUnitExportRow(
        string ScopeId,
        Guid PropertyId,
        Guid ReservationId,
        Guid InventoryUnitId,
        long ReservationVersion);

    private sealed record GuestLinkExportRow(
        string ScopeId,
        Guid PropertyId,
        Guid ReservationId,
        Guid GuestId,
        ReservationGuestRole Role,
        DateTimeOffset LinkedAtUtc,
        long LinkVersion,
        bool IsCurrent,
        DateTimeOffset? UnlinkedAtUtc,
        DateOnly? UnlinkedArrival,
        DateOnly? UnlinkedDeparture,
        ReservationState? UnlinkedReservationStatus,
        DateOnly? UnlinkedCheckedInBusinessDate,
        DateOnly? UnlinkedNoShowBusinessDate,
        DateOnly? UnlinkedCheckedOutBusinessDate,
        string LinkedBy,
        string? UnlinkedBy);
}
