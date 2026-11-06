namespace BunkFy.Modules.Reservations.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationDataRightsExportContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Staff_scope_is_not_exported_by_reservations()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            new DataRightsSubjectExportRequest(
                "tenant-a",
                DataRightsCaseType.StaffRights,
                PropertyId: null,
                new DataRightsSubjectCoordinate(
                    ReservationDataRightsDiscoveryContributor.Owner,
                    ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                    Guid.NewGuid(),
                    1)),
            sink,
            CancellationToken.None);

        Assert.Equal([DataRightsCaseType.GuestRights], contributor.SupportedCaseTypes);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Export_writes_every_owned_record_once_and_excludes_unrelated_rows()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Guid otherPropertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Maya Chen");
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(reservation.LinkGuest(
            Guid.NewGuid(),
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            reservation.Version,
            "staff:test",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Guid adapterConnectionId = Guid.NewGuid();
        Guid externalOperationId = Guid.NewGuid();
        Guid inventoryRequestId = Guid.NewGuid();
        Assert.True(reservation.BeginAllocationAmendment(
            Guid.NewGuid(),
            inventoryRequestId,
            new string('a', Reservation.RequestFingerprintLength),
            reservation.Arrival,
            reservation.Departure.AddDays(1),
            reservation.RequestedUnits.Select(unit => unit.InventoryUnitId).ToArray(),
            "Maya Chen Updated",
            "maya.updated@example.test",
            "+44 20 9999 4321",
            reservation.GuestCount,
            "Adapter note",
            reservation.DetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            "adapter:test",
            adapterConnectionId,
            externalOperationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(3)).IsSuccess);
        dbContext.Reservations.Add(reservation);
        dbContext.Reservations.Add(CreateReservation(otherPropertyId, "Other Guest"));

        dbContext.ReservationDetailsHistory.AddRange(
            CreateHistory(reservation, fromRevision: 0, toRevision: 1, Now),
            CreateHistory(reservation, fromRevision: 1, toRevision: 2, Now.AddMinutes(1)));
        dbContext.ReservationDetailsHistory.Add(
            CreateHistory(
                CreateReservation(otherPropertyId, "Unrelated History"),
                fromRevision: 0,
                toRevision: 1,
                Now));
        ReservationDataRightsCorrectionReceipt correctionReceipt =
            ReservationDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                propertyId,
                Guid.NewGuid(),
                approvalRevision: 1,
                reservation.Id,
                new(
                    PreviousRecordVersion: 1,
                    CurrentRecordVersion: 2,
                    PreviousDetailsRevision: 0,
                    CurrentDetailsRevision: 1,
                    [ReservationDetailsField.Email],
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Now.AddMinutes(3)),
                Guid.NewGuid(),
                Guid.NewGuid()).Value;
        dbContext.DataRightsCorrectionReceipts.Add(correctionReceipt);

        ReservationProcessingRestriction restriction =
            ReservationProcessingRestriction.Create(
                Guid.NewGuid(),
                "tenant-a",
                propertyId,
                reservation.Id,
                Guid.NewGuid(),
                applyApprovalRevision: 1,
                reservation.Version,
                "user:privacy-operator",
                Now.AddMinutes(4)).Value;
        ReservationProcessingRestrictionProjection restrictionState =
            ReservationProcessingRestrictionProjection.Create(
                "tenant-a",
                propertyId,
                reservation.Id,
                ReservationProcessingRestrictionContract.CurrentVersion,
                Now).Value;
        Assert.True(restrictionState.Apply(
            expectedRevision: 0,
            ReservationProcessingRestrictionContract.CurrentVersion,
            Now.AddMinutes(4)).IsSuccess);
        ReservationProcessingRestrictionReceipt restrictionReceipt =
            ReservationProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                restriction.Id,
                ReservationProcessingRestrictionAction.Apply,
                propertyId,
                reservation.Id,
                restriction.ApplyCaseId,
                restriction.ApplyApprovalRevision,
                reservation.Version,
                ReservationProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                restrictionState.Revision,
                restrictionState.IsRestricted,
                Guid.NewGuid(),
                Now.AddMinutes(4)).Value;
        dbContext.ProcessingRestrictions.Add(restriction);
        dbContext.ProcessingRestrictionProjections.Add(restrictionState);
        dbContext.ProcessingRestrictionReceipts.Add(restrictionReceipt);

        ReservationDataHold dataHold = ReservationDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            reservation.Id,
            ReservationDataHoldReasonCodes.RegulatoryRequest,
            "user:privacy-operator",
            Now.AddMinutes(5)).Value;
        ReservationDataHoldReceipt dataHoldReceipt =
            ReservationDataHoldReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                dataHold,
                BunkFy.Modules.Reservations.Domain.Models
                    .ReservationDataHoldAction.Place,
                reservation.Version,
                reservation.DetailsRevision,
                Now.AddMinutes(5)).Value;
        dbContext.DataHolds.Add(dataHold);
        dbContext.DataHoldReceipts.Add(dataHoldReceipt);

        dbContext.ExternalOperations.Add(new ReservationExternalOperation(
            new ReservationExternalOperationRecord(
                externalOperationId,
                "tenant-a",
                Guid.NewGuid(),
                adapterConnectionId,
                propertyId,
                ExternalReservationOperationKind.Amend,
                new string('b', Reservation.RequestFingerprintLength),
                ExternalReservationOperationOutcome.Accepted,
                reservation.Id,
                reservation.DetailsRevision,
                reservation.Version,
                ErrorCode: null,
                Now.AddMinutes(4))));
        dbContext.ExternalOperations.Add(new ReservationExternalOperation(
            new ReservationExternalOperationRecord(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                otherPropertyId,
                ExternalReservationOperationKind.Create,
                new string('c', Reservation.RequestFingerprintLength),
                ExternalReservationOperationOutcome.Applied,
                Guid.NewGuid(),
                DetailsRevision: 1,
                ReservationVersion: 1,
                ErrorCode: null,
                Now)));
        Guid managementOperationId = Guid.NewGuid();
        string managementRequestFingerprint =
            new('d', Reservation.RequestFingerprintLength);
        dbContext.ManagementOperations.Add(new ReservationManagementOperation(
            new ReservationManagementOperationRecord(
                managementOperationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                ReservationManagementOperationKind.StayAmendment,
                ExpectedVersion: null,
                reservation.DetailsRevision,
                BusinessDate: null,
                Now.AddMinutes(5),
                managementRequestFingerprint)));
        ReservationStayAmendmentOperation stayAmendment =
            ReservationStayAmendmentOperation.CreatePending(
                managementOperationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                inventoryRequestId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                managementRequestFingerprint,
                reservation.Arrival,
                reservation.Departure.AddDays(1),
                reservation.ExpectedArrivalTime,
                reservation.ExpectedDepartureTime,
                reservation.RequestedUnits
                    .Select(unit => unit.InventoryUnitId)
                    .ToArray(),
                reservation.DetailsRevision,
                "user:stay-operator",
                Now.AddMinutes(5)).Value;
        dbContext.StayAmendmentOperations.Add(stayAmendment);

        dbContext.ArrivalReminders.Add(ReservationArrivalReminder.Create(
            Guid.NewGuid(),
            "tenant-a",
            reservation.Id,
            propertyId,
            reservation.DetailsRevision,
            "Europe/Moscow",
            reservation.Arrival,
            new TimeOnly(15, 0),
            Now.AddDays(1),
            Now.AddHours(22),
            leadTimeMinutes: 120));
        dbContext.ArrivalReminders.Add(ReservationArrivalReminder.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            otherPropertyId,
            detailsRevision: 1,
            "UTC",
            new DateOnly(2026, 8, 1),
            new TimeOnly(15, 0),
            Now.AddDays(1),
            Now.AddHours(22),
            leadTimeMinutes: 120));
        await dbContext.SaveChangesAsync();

        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            CreateRequest(propertyId, reservation),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        Assert.Equal(15, result.RecordCount);
        Assert.Equal(15, sink.Records.Count);
        Assert.Equal(
            [
                ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                ReservationDataRightsExportContributor.PendingAmendmentRecordType,
                ReservationDataRightsExportContributor.GuestLinkRecordType,
                ReservationDataRightsExportContributor.DetailsHistoryRecordType,
                ReservationDataRightsExportContributor.DetailsHistoryRecordType,
                ReservationDataRightsExportContributor.DataRightsCorrectionReceiptRecordType,
                ReservationDataRightsExportContributor.ProcessingRestrictionRecordType,
                ReservationDataRightsExportContributor.ProcessingRestrictionStateRecordType,
                ReservationDataRightsExportContributor.ProcessingRestrictionReceiptRecordType,
                ReservationDataRightsExportContributor.DataHoldRecordType,
                ReservationDataRightsExportContributor.DataHoldReceiptRecordType,
                ReservationDataRightsExportContributor.ExternalOperationRecordType,
                ReservationDataRightsExportContributor.ManagementOperationRecordType,
                ReservationDataRightsExportContributor.StayAmendmentOperationRecordType,
                ReservationDataRightsExportContributor.ArrivalReminderRecordType
            ],
            sink.Records.Select(record => record.RecordType));
        DataRightsExportRecord managementOperation = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor
                    .ManagementOperationRecordType);
        Assert.Equal(managementOperationId, managementOperation.RecordId);
        Assert.Equal(3, managementOperation.RecordVersion);
        Assert.Equal(
            managementRequestFingerprint,
            Assert.Single(
                managementOperation.Fields,
                field => field.FieldId ==
                    "reservation.management-operation.request-fingerprint")
                .Value.GetString());
        Assert.DoesNotContain(
            managementOperation.Fields,
            field => field.FieldId is
                "reservation.audit.actor-id" or
                "reservation.guest.primary-name" or
                "reservation.guest.email" or
                "reservation.guest.phone" or
                "reservation.guest.notes");
        Assert.DoesNotContain(
            sink.Records.SelectMany(record => record.Fields),
            field => field.FieldId == "reservation.audit.actor-id");
        DataRightsExportRecord reservationExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsDiscoveryContributor.ReservationRecordType);
        Assert.Equal(
            inventoryRequestId,
            Assert.Single(
                reservationExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.inventory-request-id")
                .Value.GetGuid());
        DataRightsExportRecord stayAmendmentExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor
                    .StayAmendmentOperationRecordType);
        Assert.Equal(
            DataRightsExportRecordIds.CreateDeterministicChild(
                reservation.Id,
                managementOperationId.ToString("N")),
            stayAmendmentExport.RecordId);
        Assert.Equal(
            stayAmendment.OperationVersion,
            stayAmendmentExport.RecordVersion);
        Assert.Equal(
            inventoryRequestId,
            Assert.Single(
                stayAmendmentExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.inventory-request-id")
                .Value.GetGuid());
        Assert.Equal(
            "pending",
            Assert.Single(
                stayAmendmentExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.outcome")
                .Value.GetString());
        Assert.Equal(
            "2026-08-04",
            Assert.Single(
                stayAmendmentExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.target-departure")
                .Value.GetString());
        Assert.DoesNotContain(
            stayAmendmentExport.Fields,
            field => field.FieldId is
                "reservation.stay-amendment.requested-by" or
                "reservation.stay-amendment.last-reconciled-by");
        DataRightsExportRecord correctionExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor.DataRightsCorrectionReceiptRecordType);
        Assert.DoesNotContain(
            correctionExport.Fields,
            field => field.FieldId is
                "reservation.data-rights.idempotency-key" or
                "reservation.data-rights.correlation-id");
        DataRightsExportRecord restrictionExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor.ProcessingRestrictionRecordType);
        DataRightsExportRecord restrictionReceiptExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor
                    .ProcessingRestrictionReceiptRecordType);
        Assert.DoesNotContain(
            restrictionExport.Fields.Concat(restrictionReceiptExport.Fields),
            field => field.FieldId is
                "reservation.processing-restriction.actor-id" or
                "reservation.processing-restriction.idempotency-key" or
                "reservation.guest.primary-name" or
                "reservation.guest.email" or
                "reservation.guest.phone" or
                "reservation.guest.notes");
        DataRightsExportRecord dataHoldExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor.DataHoldRecordType);
        DataRightsExportRecord dataHoldReceiptExport = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor.DataHoldReceiptRecordType);
        Assert.DoesNotContain(
            dataHoldExport.Fields.Concat(dataHoldReceiptExport.Fields),
            field => field.FieldId is
                "reservation.data-hold.actor-id" or
                "reservation.data-hold.idempotency-key" or
                "reservation.guest.primary-name" or
                "reservation.guest.email" or
                "reservation.guest.phone" or
                "reservation.guest.notes");
        Assert.All(
            sink.Records,
            record => Assert.InRange(
                record.Fields.Count,
                1,
                DataRightsExportLimits.MaxFieldsPerRecord));
    }

    [Fact]
    public async Task Export_rejects_stale_cross_property_and_unknown_scope_without_writes()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Guid otherPropertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Guest");
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        CollectingSink staleSink = new();
        DataRightsSubjectExportResult stale = await contributor.ExportAsync(
            CreateRequest(propertyId, reservation) with
            {
                Coordinate = CreateRequest(propertyId, reservation).Coordinate with
                {
                    RecordVersion = reservation.Version + 1
                }
            },
            staleSink,
            CancellationToken.None);
        CollectingSink otherPropertySink = new();
        DataRightsSubjectExportResult otherProperty = await contributor.ExportAsync(
            CreateRequest(otherPropertyId, reservation),
            otherPropertySink,
            CancellationToken.None);
        CollectingSink unknownPropertySink = new();
        DataRightsSubjectExportResult unknownProperty = await contributor.ExportAsync(
            CreateRequest(Guid.NewGuid(), reservation),
            unknownPropertySink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Stale, stale.Status);
        Assert.Equal(DataRightsSubjectExportStatus.NotFound, otherProperty.Status);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, unknownProperty.Status);
        Assert.Empty(staleSink.Records);
        Assert.Empty(otherPropertySink.Records);
        Assert.Empty(unknownPropertySink.Records);
    }

    [Fact]
    public async Task Guest_export_excludes_seeded_station_staff_attribution()
    {
        await using ReservationsDbContext db = CreateDbContext("tenant-a");
        Guid property = AddKnownProperty(db);
        Reservation reservation = CreateReservation(property, "Synthetic Station Guest");
        Guid operationId = Guid.NewGuid();
        var provenance = new StationCheckInProvenance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 3, StationReservationAuthority.StationOnly);
        db.Reservations.Add(reservation);
        db.ManagementOperations.Add(new ReservationManagementOperation(new ReservationManagementOperationRecord(
            operationId, "tenant-a", property, reservation.Id, ReservationManagementOperationKind.CheckIn,
            reservation.Version, null, reservation.Arrival, Now)));
        db.StationAttributions.Add(new ReservationStationAttribution("tenant-a", reservation.Id, operationId,
            provenance, reservation.Version + 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Single(await db.StationAttributions.ToArrayAsync());
        var contributor = new ReservationDataRightsExportContributor(db, new TestScopeContext("tenant-a"));
        var sink = new CollectingSink();
        var result = await contributor.ExportAsync(CreateRequest(property, reservation), sink, CancellationToken.None);
        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        var management = Assert.Single(sink.Records, row => row.RecordType == ReservationDataRightsExportContributor.ManagementOperationRecordType);
        Assert.Equal(3, management.RecordVersion);
        Assert.DoesNotContain(sink.Records.SelectMany(row => row.Fields), field =>
            field.FieldId.Contains("station", StringComparison.OrdinalIgnoreCase) ||
            field.FieldId.Contains("staff-attribution", StringComparison.OrdinalIgnoreCase));
        string json = JsonSerializer.Serialize(sink.Records);
        foreach (Guid employeeCoordinate in new[] { provenance.StationId, provenance.BrowserSessionId, provenance.StaffMemberId, provenance.ActorSessionId })
        {
            Assert.DoesNotContain(employeeCoordinate.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Export_preserves_truthful_unknown_legacy_stay_amendment_evidence()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Legacy Guest");
        Guid operationId = Guid.NewGuid();
        string fingerprint = new('e', Reservation.RequestFingerprintLength);
        dbContext.Reservations.Add(reservation);
        dbContext.ManagementOperations.Add(new ReservationManagementOperation(
            new ReservationManagementOperationRecord(
                operationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                ReservationManagementOperationKind.StayAmendment,
                ExpectedVersion: null,
                reservation.DetailsRevision,
                BusinessDate: null,
                Now,
                fingerprint)));
        dbContext.StayAmendmentOperations.Add(
            ReservationStayAmendmentOperation.CreateOutcomeUnknown(
                operationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                fingerprint,
                reservation.DetailsRevision,
                Now).Value);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            CreateRequest(propertyId, reservation),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        DataRightsExportRecord operation = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor
                    .StayAmendmentOperationRecordType);
        Assert.Equal(
            "outcome-unknown",
            Assert.Single(
                operation.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.outcome")
                .Value.GetString());
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(
                operation.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.target-arrival")
                .Value.ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(
                operation.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.target-inventory-unit-ids")
                .Value.ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(
                operation.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.inventory-request-id")
                .Value.ValueKind);
        Assert.DoesNotContain(
            operation.Fields,
            field => field.FieldId is
                "reservation.stay-amendment.requested-by" or
                "reservation.stay-amendment.last-reconciled-by");
    }

    [Fact]
    public async Task Export_retains_terminal_inventory_request_identity_and_nulls_applied_no_op()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Terminal Guest");
        Guid terminalOperationId = Guid.NewGuid();
        Guid noOpOperationId = Guid.NewGuid();
        Guid inventoryRequestId = Guid.NewGuid();
        string terminalFingerprint = new('f', Reservation.RequestFingerprintLength);
        string noOpFingerprint = new('a', Reservation.RequestFingerprintLength);
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now).IsSuccess);
        dbContext.Reservations.Add(reservation);
        dbContext.ManagementOperations.AddRange(
            new ReservationManagementOperation(
                new ReservationManagementOperationRecord(
                    terminalOperationId,
                    "tenant-a",
                    propertyId,
                    reservation.Id,
                    ReservationManagementOperationKind.StayAmendment,
                    ExpectedVersion: null,
                    reservation.DetailsRevision,
                    BusinessDate: null,
                    Now,
                    terminalFingerprint)),
            new ReservationManagementOperation(
                new ReservationManagementOperationRecord(
                    noOpOperationId,
                    "tenant-a",
                    propertyId,
                    reservation.Id,
                    ReservationManagementOperationKind.StayAmendment,
                    ExpectedVersion: null,
                    reservation.DetailsRevision,
                    BusinessDate: null,
                    Now.AddMinutes(2),
                    noOpFingerprint)));
        ReservationStayAmendmentOperation terminal =
            ReservationStayAmendmentOperation.CreatePending(
                terminalOperationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                inventoryRequestId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                terminalFingerprint,
                reservation.Arrival,
                reservation.Departure.AddDays(1),
                reservation.ExpectedArrivalTime,
                reservation.ExpectedDepartureTime,
                reservation.RequestedUnits
                    .Select(unit => unit.InventoryUnitId)
                    .ToArray(),
                reservation.DetailsRevision,
                "user:stay-operator",
                Now).Value;
        Assert.True(terminal.MarkRejected(
            rejectionCode: 1,
            reservation.DetailsRevision,
            reservation.Version,
            Now.AddMinutes(1)).IsSuccess);
        ReservationStayAmendmentOperation noOp =
            ReservationStayAmendmentOperation.CreateAppliedNoOp(
                noOpOperationId,
                "tenant-a",
                propertyId,
                reservation.Id,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                noOpFingerprint,
                reservation.Arrival,
                reservation.Departure,
                reservation.ExpectedArrivalTime,
                reservation.ExpectedDepartureTime,
                reservation.RequestedUnits
                    .Select(unit => unit.InventoryUnitId)
                    .ToArray(),
                reservation.DetailsRevision,
                "user:stay-operator",
                reservation.DetailsRevision,
                reservation.Version,
                reservation.AllocationVersion!.Value,
                Now.AddMinutes(2)).Value;
        dbContext.StayAmendmentOperations.AddRange(terminal, noOp);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            CreateRequest(propertyId, reservation),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        DataRightsExportRecord terminalExport = Assert.Single(
            sink.Records,
            record => record.RecordId ==
                DataRightsExportRecordIds.CreateDeterministicChild(
                    reservation.Id,
                    terminalOperationId.ToString("N")));
        Assert.Equal(
            inventoryRequestId,
            Assert.Single(
                terminalExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.inventory-request-id")
                .Value.GetGuid());
        Assert.Equal(
            "rejected",
            Assert.Single(
                terminalExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.outcome")
                .Value.GetString());
        DataRightsExportRecord noOpExport = Assert.Single(
            sink.Records,
            record => record.RecordId ==
                DataRightsExportRecordIds.CreateDeterministicChild(
                    reservation.Id,
                    noOpOperationId.ToString("N")));
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(
                noOpExport.Fields,
                field => field.FieldId ==
                    "reservation.stay-amendment.inventory-request-id")
                .Value.ValueKind);
        Assert.DoesNotContain(
            terminalExport.Fields.Concat(noOpExport.Fields),
            field => field.FieldId is
                "reservation.stay-amendment.requested-by" or
                "reservation.stay-amendment.last-reconciled-by");
    }

    [Fact]
    public async Task Export_includes_minimum_anonymisation_owner_proof()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Maya Chen");
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.UnitNotSellable,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            "user:privacy-executor",
            Guid.NewGuid(),
            Now.AddMinutes(2)).Value;
        ReservationAnonymisationReceipt receipt =
            ReservationAnonymisationReceipt.Create(
                Guid.NewGuid(),
                reservation.ScopeId,
                Guid.NewGuid(),
                propertyId,
                Guid.NewGuid(),
                approvalRevision: 2,
                operationRevision: 3,
                reservation.Id,
                outcome,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0,
                new string('a', ReservationAnonymisationReceipt.Sha256Length),
                new string('b', ReservationAnonymisationReceipt.Sha256Length)).Value;
        dbContext.Reservations.Add(reservation);
        dbContext.AnonymisationReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            CreateRequest(propertyId, reservation),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        DataRightsExportRecord exportedReceipt = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                ReservationDataRightsExportContributor
                    .AnonymisationReceiptRecordType);
        Assert.DoesNotContain(
            exportedReceipt.Fields,
            field => field.FieldId is
                "reservation.anonymisation.actor-id" or
                "reservation.anonymisation.idempotency-key");
        Assert.Contains(
            exportedReceipt.Fields,
            field => field.FieldId ==
                "reservation.anonymisation.owner-approval-revision");
        Assert.Contains(
            exportedReceipt.Fields,
            field => field.FieldId ==
                "reservation.anonymisation.canonical-sha256");
    }

    [Fact]
    public async Task Sink_failure_propagates_without_a_false_success_result()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext);
        Reservation reservation = CreateReservation(propertyId, "Guest");
        dbContext.Reservations.Add(reservation);
        dbContext.ReservationDetailsHistory.Add(CreateHistory(
            reservation,
            fromRevision: 0,
            toRevision: 1,
            Now));
        await dbContext.SaveChangesAsync();
        ReservationDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        FailingSink sink = new(failOnWrite: 2);

        await Assert.ThrowsAsync<IOException>(() => contributor.ExportAsync(
            CreateRequest(propertyId, reservation),
            sink,
            CancellationToken.None));

        Assert.Single(sink.Records);
    }

    [Fact]
    public void Export_schema_is_catalogue_bound_and_versioned()
    {
        ReservationDataRightsExportSchema.EnsureValid();

        DataRightsExportDescriptor descriptor =
            ReservationDataRightsExportSchema.Descriptor;
        Assert.Equal(ReservationDataRightsDiscoveryContributor.Owner, descriptor.OwnerKey);
        Assert.Equal("reservations.personal-data", descriptor.CatalogId);
        Assert.Equal(24, descriptor.CatalogVersion);
        Assert.Equal("reservations.subject-export", descriptor.ExportSchemaId);
        Assert.Equal(9, descriptor.ExportSchemaVersion);
        Assert.NotEmpty(descriptor.FieldIds);
    }

    private static DataRightsSubjectExportRequest CreateRequest(
        Guid propertyId,
        Reservation reservation) =>
        new(
            "tenant-a",
            DataRightsCaseType.GuestRights,
            propertyId,
            new(
                ReservationDataRightsDiscoveryContributor.Owner,
                ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                reservation.Id,
                reservation.Version));

    private static ReservationDetailsHistoryEntry CreateHistory(
        Reservation reservation,
        long fromRevision,
        long toRevision,
        DateTimeOffset occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            "tenant-a",
            reservation.Id,
            reservation.PropertyId,
            fromRevision,
            toRevision,
            ReservationDetailsChangeOrigin.Staff,
            actorId: "staff:test",
            adapterConnectionId: null,
            externalOperationId: null,
            operationDeduplicationKey: new string('d', 64),
            Guid.NewGuid(),
            changedFieldsJson: "[\"PrimaryGuestName\"]",
            beforeSnapshotJson: fromRevision == 0 ? null : "{\"primaryGuestName\":\"Before\"}",
            afterSnapshotJson: "{\"primaryGuestName\":\"After\"}",
            afterSnapshotHash: new string('e', 64),
            occurredAtUtc);

    private static Reservation CreateReservation(
        Guid propertyId,
        string primaryGuestName) =>
        Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
            primaryGuestName,
            "guest@example.test",
            "+44 20 1234 5678",
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: "Late arrival",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now).Value;

    private static Guid AddKnownProperty(ReservationsDbContext dbContext)
    {
        Guid propertyId = Guid.NewGuid();
        ReservationPropertyProjection property =
            ReservationPropertyProjection.Create(propertyId, "tenant-a");
        property.ApplyTopology("UTC", isActive: true, sourceVersion: 1);
        dbContext.PropertyProjections.Add(property);
        return propertyId;
    }

    private static ReservationsDbContext CreateDbContext(string tenantId)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase($"reservations-data-rights-export-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSink(int failOnWrite) : IDataRightsExportSink
    {
        private int writeCount;

        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.writeCount++;
            if (this.writeCount == failOnWrite)
            {
                throw new IOException("The test sink failed.");
            }

            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
