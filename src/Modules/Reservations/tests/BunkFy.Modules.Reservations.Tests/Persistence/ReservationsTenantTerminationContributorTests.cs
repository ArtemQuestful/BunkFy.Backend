namespace BunkFy.Modules.Reservations.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using BunkFy.Modules.Reservations.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainDataHoldAction =
    BunkFy.Modules.Reservations.Domain.Models.ReservationDataHoldAction;

[Trait("Category", "Unit")]
public sealed class ReservationsTenantTerminationContributorTests
{
    private const string TenantId =
        ReservationsTenantTerminationTestData.TenantId;
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ProcessId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid StayInventoryRequestId =
        Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        ReservationsTenantTerminationTestData.Now;
    private static readonly DateTimeOffset FrozenAtUtc =
        Now.AddMinutes(-1);

    [Fact]
    public async Task Export_streams_complete_owner_state_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(
            "reservations.termination.exported",
            result.ResultCode);
        Assert.Equal(first.Records.Count, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            ReservationsTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            first.Records,
            record => Field(record, "reservations.property-id")
                .GetGuid() == OtherPropertyId);
        Assert.Contains(
            first.Records.Where(record =>
                record.RecordType ==
                    ReservationsTenantTerminationMetadata
                        .ReservationRecordType),
            record => Field(
                    record,
                    "reservations.staff-attribution")
                .GetProperty("lastDetailsActorId")
                .GetString() == "user:owner");
        DataRightsExportRecord pendingReservation = Assert.Single(
            first.Records,
            record => record.RecordType ==
                    ReservationsTenantTerminationMetadata.ReservationRecordType &&
                Field(record, "reservations.booking-state")
                    .GetProperty("pendingInventoryAmendmentRequestId")
                    .ValueKind != JsonValueKind.Null);
        Assert.Equal(
            StayInventoryRequestId,
            Field(pendingReservation, "reservations.booking-state")
                .GetProperty("pendingInventoryAmendmentRequestId")
                .GetGuid());
        DataRightsExportRecord managementOperation = Assert.Single(
            first.Records,
            record => record.RecordType ==
                ReservationsTenantTerminationMetadata
                    .ManagementOperationRecordType);
        Guid managementReservationId = Field(
            managementOperation,
            "reservations.reservation-id").GetGuid();
        Guid managementOperationId = Field(
            managementOperation,
            "reservations.record-id").GetGuid();
        Assert.Equal(
            DataRightsExportRecordIds.CreateDeterministicChild(
                managementReservationId,
                managementOperationId.ToString("N")),
            managementOperation.RecordId);
        Assert.Equal(5, managementOperation.RecordVersion);
        Assert.Equal(JsonValueKind.Null, Field(managementOperation, "reservations.staff-attribution").ValueKind);
        Assert.Equal(
            Digest,
            Field(managementOperation, "reservations.management-operation")
                .GetProperty("requestFingerprint")
                .GetString());
        DataRightsExportRecord stayAmendmentOperation = Assert.Single(
            first.Records,
            record => record.RecordType ==
                ReservationsTenantTerminationMetadata
                    .StayAmendmentOperationRecordType);
        Assert.Equal(
            "pending",
            Field(
                    stayAmendmentOperation,
                    "reservations.stay-amendment-operation")
                .GetProperty("outcome")
                .GetString());
        Assert.Equal(
            StayInventoryRequestId,
            Field(
                    stayAmendmentOperation,
                    "reservations.stay-amendment-operation")
                .GetProperty("inventoryRequestId")
                .GetGuid());
        Assert.Equal(
            "user:stay-operator",
            Field(stayAmendmentOperation, "reservations.staff-attribution")
                .GetProperty("requestedBy")
                .GetString());
        DataRightsExportRecord guestRecordLinkProcess = Assert.Single(
            first.Records,
            record => record.RecordType ==
                ReservationsTenantTerminationMetadata
                    .GuestRecordLinkProcessRecordType);
        JsonElement processState = Field(
            guestRecordLinkProcess,
            "reservations.guest-record-link-process");
        Assert.Equal(1, processState.GetProperty("revision").GetInt64());
        Assert.Equal(
            "user:owner",
            Field(guestRecordLinkProcess, "reservations.staff-attribution")
                .GetProperty("requestedBy")
                .GetString());
        Assert.Equal(
            ReservationsTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(8, contributor.ExportDescriptor.CatalogVersion);
        Assert.Equal(10, contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.Equal(
            24,
            ReservationsTenantTerminationMetadata.PersonalDataCatalogVersion);
        Assert.Equal(
            ReservationsTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Tenant_export_disambiguates_reservation_local_stay_amendment_ids_and_preserves_unknowns()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        Reservation pendingReservation =
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId,
                "Pending Guest");
        Reservation unknownReservation =
            ReservationsTenantTerminationTestData.CreateReservation(
                OtherPropertyId,
                "Legacy Guest");
        Guid sharedOperationId = Guid.NewGuid();
        Guid pendingInventoryRequestId = Guid.NewGuid();
        AddStayAmendmentOperation(
            context,
            pendingReservation,
            sharedOperationId,
            pendingInventoryRequestId,
            outcomeUnknown: false);
        AddStayAmendmentOperation(
            context,
            unknownReservation,
            sharedOperationId,
            inventoryRequestId: null,
            outcomeUnknown: true);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result = await contributor
            .ExportAsync(Request(), sink, CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        DataRightsExportRecord[] records = sink.Records
            .Where(record => record.RecordType ==
                ReservationsTenantTerminationMetadata
                    .StayAmendmentOperationRecordType)
            .ToArray();
        Assert.Equal(2, records.Length);
        Guid[] expectedReservationOrder =
        [pendingReservation.Id, unknownReservation.Id];
        Array.Sort(expectedReservationOrder);
        Assert.Equal(
            expectedReservationOrder,
            records
                .Select(record =>
                    Field(record, "reservations.reservation-id").GetGuid())
                .ToArray());
        Assert.Equal(
            expectedReservationOrder
                .Select(reservationId =>
                    DataRightsExportRecordIds.CreateDeterministicChild(
                        reservationId,
                        sharedOperationId.ToString("N")))
                .ToArray(),
            records.Select(record => record.RecordId).ToArray());
        Assert.Equal(2, records.Select(record => record.RecordId).Distinct().Count());
        Assert.All(
            records,
            record => Assert.Equal(
                sharedOperationId,
                Field(record, "reservations.record-id").GetGuid()));
        DataRightsExportRecord pending = Assert.Single(
            records,
            record => Field(
                    record,
                    "reservations.stay-amendment-operation")
                .GetProperty("outcome")
                .GetString() == "pending");
        Assert.Equal(
            pendingInventoryRequestId,
            Field(pending, "reservations.stay-amendment-operation")
                .GetProperty("inventoryRequestId")
                .GetGuid());
        DataRightsExportRecord unknown = Assert.Single(
            records,
            record => Field(
                    record,
                    "reservations.stay-amendment-operation")
                .GetProperty("outcome")
                .GetString() == "outcome-unknown");
        JsonElement unknownState = Field(
            unknown,
            "reservations.stay-amendment-operation");
        Assert.Equal(JsonValueKind.Null, unknownState.GetProperty("targetArrival").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            unknownState.GetProperty("targetInventoryUnitIds").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            unknownState.GetProperty("inventoryRequestId").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(unknown, "reservations.staff-attribution")
                .GetProperty("requestedBy")
                .ValueKind);
    }

    [Fact]
    public async Task Export_retries_without_records_for_a_different_fence()
    {
        MutableFenceReader fences = new()
        {
            Current = FrozenFence() with { Version = 2 }
        };
        await using ReservationsDbContext context = CreateContext(fences);
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "reservations.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Operational_save_rejects_a_frozen_workspace_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        context.Reservations.Add(
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        context.Reservations.Add(
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId,
                "Blocked Guest"));

        ReservationsOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                ReservationsOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            ReservationsOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.Single(await context.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        context.Reservations.Add(
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId));
        await context.SaveChangesAsync();

        context.Reservations.Add(
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId,
                "Second Guest"));
        await context.SaveChangesAsync();

        Assert.Equal(
            2,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Operational_save_fails_closed_when_fence_read_fails()
    {
        await using ReservationsDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.Reservations.Add(
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId));

        ReservationsOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                ReservationsOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            ReservationsOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Reservations.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Fact]
    public async Task Destroy_blocks_an_active_hold_before_opening_local_progress()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(Now.AddHours(1)),
            fences);

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(
                DestroyRequest(),
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Blocked,
            result.Status);
        Assert.Equal(
            "reservations.termination.destroy-active-hold",
            result.ResultCode);
        Assert.Equal(1, result.RemainingActiveCount);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        Assert.Empty(await context.TenantDestroyReceipts.ToListAsync());
        ReservationsTenantRevision state =
            await context.TenantRevisions.SingleAsync();
        Assert.True(state.IsOpen);
        Assert.Null(state.DestroyOperationId);
    }

    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        ReservationDataHold hold = await context.DataHolds.SingleAsync();
        Assert.True(hold.Release(
            hold.Version,
            "user:privacy-controller",
            Now.AddMinutes(8)).IsSuccess);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        TestClock clock = new(Now.AddHours(1));
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            clock,
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        int attempts = 1;
        while (result.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 100)
        {
            result = await contributor.ExecuteAsync(
                request,
                CancellationToken.None);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(
            "reservations.termination.destroyed",
            result.ResultCode);
        Assert.True(result.AffectedCount > 0);
        Assert.Equal(2, result.SelectedProofRevision);
        Assert.Equal(3, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        ReservationsTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            ReservationsTenantLifecycleStatus.Closed,
            (await context.TenantRevisions.SingleAsync()).LifecycleStatus);
        Assert.False(await HasOwnerRecordsAsync(context));

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(result, replay);

        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "reservations.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);
        context.ChangeTracker.Clear();
        fences.Current = null;
        context.PropertyProjections.Add(
            ReservationPropertyProjection.Create(PropertyId, TenantId));
        ReservationsOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<ReservationsOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            ReservationsOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);

        context.ChangeTracker.Clear();
        context.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.reservations.lifecycle-test.v1",
            "lifecycle-test",
            version: 1,
            TenantId,
            Now,
            "{}",
            Now));
        ReservationsOperationalAdmissionException messageFailure =
            await Assert.ThrowsAsync<ReservationsOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            ReservationsOperationalAdmissionFailure.Restricted,
            messageFailure.Failure);
    }

    [Fact]
    public async Task Destroy_counts_stay_amendment_child_separately_from_its_parent()
    {
        MutableFenceReader fences = new();
        await using ReservationsDbContext context = CreateContext(fences);
        Reservation reservation =
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId,
                "Destroy Guest");
        reservation.ClearDomainEvents();
        AddStayAmendmentOperation(
            context,
            reservation,
            Guid.NewGuid(),
            Guid.NewGuid(),
            outcomeUnknown: false);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        ReservationsTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(Now.AddHours(1)),
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult result = await contributor
            .ExecuteAsync(request, CancellationToken.None);
        for (int attempt = 1;
             result.Status == TenantTerminationContributionStatus.RetryRequired &&
             attempt < 100;
             attempt++)
        {
            result = await contributor.ExecuteAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(4, result.AffectedCount);
        Assert.Equal(
            result.AffectedCount,
            (await context.TenantDestroyReceipts.SingleAsync())
                .RemovedRecordCount);
        Assert.Empty(await context.StayAmendmentOperations.ToListAsync());
        Assert.Empty(await context.ManagementOperations.ToListAsync());
        Assert.Empty(await context.RequestedInventoryUnits.ToListAsync());
        Assert.Empty(await context.Reservations.ToListAsync());
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        ReservationsTenantDestroyOperation operation = Assert.IsType<
            ReservationsTenantDestroyOperation>(
            ReservationsTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                ReservationsTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            ReservationsTenantDestroyStage.OutboxMessages,
            ReservationsTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            ReservationsTenantDestroyStage.OutboxMessages,
            ReservationsTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            ReservationsTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    private static void SeedGraph(ReservationsDbContext context)
    {
        Reservation reservation =
            ReservationsTenantTerminationTestData.CreateReservation(
                PropertyId);
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
            "user:owner",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Guid adapterConnectionId = Guid.NewGuid();
        Guid externalOperationId = Guid.NewGuid();
        Assert.True(reservation.BeginAllocationAmendment(
            Guid.NewGuid(),
            StayInventoryRequestId,
            new string('a', Reservation.RequestFingerprintLength),
            reservation.Arrival,
            reservation.Departure.AddDays(1),
            reservation.RequestedUnits
                .Select(unit => unit.InventoryUnitId)
                .ToArray(),
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
        context.Reservations.Add(reservation);
        context.GuestRecordLinkProcesses.Add(
            ReservationGuestRecordLinkProcess.Prepare(
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                TenantId,
                PropertyId,
                reservation.Id,
                Guid.Parse("80000000-0000-0000-0000-000000000002"),
                reservation.Version,
                "user:owner",
                Now.AddMinutes(6)).Value);
        context.ReservationDetailsHistory.Add(new(
            Guid.NewGuid(),
            TenantId,
            reservation.Id,
            reservation.PropertyId,
            fromRevision: 0,
            toRevision: 1,
            ReservationDetailsChangeOrigin.Staff,
            actorId: "user:owner",
            adapterConnectionId: null,
            externalOperationId: null,
            operationDeduplicationKey: new string('d', 64),
            Guid.NewGuid(),
            changedFieldsJson: "[\"PrimaryGuestName\"]",
            beforeSnapshotJson: null,
            afterSnapshotJson: "{\"primaryGuestName\":\"Maya\"}",
            afterSnapshotHash: new string('e', 64),
            Now.AddMinutes(1)));
        context.ExternalOperations.Add(new ReservationExternalOperation(
            new ReservationExternalOperationRecord(
                externalOperationId,
                TenantId,
                Guid.NewGuid(),
                adapterConnectionId,
                PropertyId,
                ExternalReservationOperationKind.Amend,
                new string('b', Reservation.RequestFingerprintLength),
                ExternalReservationOperationOutcome.Accepted,
                reservation.Id,
                reservation.DetailsRevision,
                reservation.Version,
                ErrorCode: null,
                Now.AddMinutes(4))));
        Guid stayAmendmentOperationId = Guid.NewGuid();
        context.ManagementOperations.Add(new ReservationManagementOperation(
            new ReservationManagementOperationRecord(
                stayAmendmentOperationId,
                TenantId,
                PropertyId,
                reservation.Id,
                ReservationManagementOperationKind.StayAmendment,
                ExpectedVersion: null,
                reservation.DetailsRevision,
                BusinessDate: null,
                Now.AddMinutes(5),
                Digest)));
        context.StayAmendmentOperations.Add(
            ReservationStayAmendmentOperation.CreatePending(
                stayAmendmentOperationId,
                TenantId,
                PropertyId,
                reservation.Id,
                StayInventoryRequestId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                Digest,
                reservation.Arrival,
                reservation.Departure.AddDays(1),
                reservation.ExpectedArrivalTime,
                reservation.ExpectedDepartureTime,
                reservation.RequestedUnits
                    .Select(unit => unit.InventoryUnitId)
                    .ToArray(),
                reservation.DetailsRevision,
                "user:stay-operator",
                Now.AddMinutes(5)).Value);
        context.ArrivalReminders.Add(ReservationArrivalReminder.Create(
            Guid.NewGuid(),
            TenantId,
            reservation.Id,
            PropertyId,
            reservation.DetailsRevision,
            "Europe/Moscow",
            reservation.Arrival,
            new TimeOnly(15, 0),
            Now.AddDays(1),
            Now.AddHours(22),
            leadTimeMinutes: 120));

        ReservationDataRightsCorrectionReceipt correction =
            ReservationDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                PropertyId,
                CaseId,
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
        context.DataRightsCorrectionReceipts.Add(correction);

        ReservationProcessingRestriction restriction =
            ReservationProcessingRestriction.Create(
                Guid.NewGuid(),
                TenantId,
                PropertyId,
                reservation.Id,
                CaseId,
                applyApprovalRevision: 1,
                reservation.Version,
                "user:owner",
                Now.AddMinutes(4)).Value;
        ReservationProcessingRestrictionProjection restrictionProjection =
            ReservationProcessingRestrictionProjection.Create(
                TenantId,
                PropertyId,
                reservation.Id,
                ReservationProcessingRestrictionContract.CurrentVersion,
                Now).Value;
        Assert.True(restrictionProjection.Apply(
            expectedRevision: 0,
            ReservationProcessingRestrictionContract.CurrentVersion,
            Now.AddMinutes(4)).IsSuccess);
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionProjections.Add(
            restrictionProjection);
        context.ProcessingRestrictionReceipts.Add(
            ReservationProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                restriction.Id,
                ReservationProcessingRestrictionAction.Apply,
                PropertyId,
                reservation.Id,
                CaseId,
                approvalRevision: 1,
                reservation.Version,
                ReservationProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                restrictionProjection.Revision,
                restrictionProjection.IsRestricted,
                Guid.NewGuid(),
                Now.AddMinutes(4)).Value);

        ReservationDataHold hold = ReservationDataHold.Place(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            reservation.Id,
            ReservationDataHoldReasonCodes.RegulatoryRequest,
            "user:owner",
            Now.AddMinutes(5)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(ReservationDataHoldReceipt.Create(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            hold,
            DomainDataHoldAction.Place,
            reservation.Version,
            reservation.DetailsRevision,
            Now.AddMinutes(5)).Value);

        SeedAnonymisationProof(context);
        SeedRetentionProof(context);
    }

    private static void SeedAnonymisationProof(
        ReservationsDbContext context)
    {
        Reservation reservation =
            ReservationRetentionModelTests.CreateTerminalReservation(
                TenantId,
                OtherPropertyId);
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            "user:privacy-executor",
            Guid.NewGuid(),
            Now.AddMinutes(6)).Value;
        ReservationAnonymisationReceipt receipt =
            ReservationAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                OtherPropertyId,
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                reservation.Id,
                outcome,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0,
                Digest,
                Digest).Value;
        ReservationAnonymisationTombstone tombstone =
            ReservationAnonymisationTombstone.Create(receipt).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = Now.AddMinutes(7);
        Assert.True(tombstone.AttachRestoreProof(
            OtherPropertyId,
            receipt.ContractVersion,
            receipt.Id,
            receipt.CanonicalSha256,
            receipt.ResultingReservationVersion,
            receipt.ResultingDetailsRevision,
            receipt.CompletedAtUtc,
            ledgerEntryId,
            replayedAtUtc).IsSuccess);
        ReservationAnonymisationRestoreReceipt restoreReceipt =
            ReservationAnonymisationRestoreReceipt.Create(
                TenantId,
                ledgerEntryId,
                tenantSequence: 1,
                Digest,
                OtherPropertyId,
                reservation.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingReservationVersion,
                receipt.ResultingDetailsRevision,
                receipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.Reservations.Add(reservation);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
    }

    private static void SeedRetentionProof(ReservationsDbContext context)
    {
        Reservation reservation =
            ReservationRetentionModelTests.CreateTerminalReservation(
                TenantId,
                PropertyId);
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
                Guid.NewGuid(),
                TenantId,
                "reservation-operational",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now.AddMinutes(-2),
                Now.AddMinutes(10)).Value;
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            ReservationRetentionAnonymisationReceipt.SystemActorId,
            Guid.NewGuid(),
            Now).Value;
        ReservationRetentionAnonymisationReceipt receipt =
            ReservationRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                execution.Id,
                PropertyId,
                reservation.Id,
                outcome,
                reservation.TerminalAtUtc!.Value,
                Now.AddDays(-1),
                Digest,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0).Value;

        context.Reservations.Add(reservation);
        context.RetentionExecutions.Add(execution);
        context.RetentionAnonymisationReceipts.Add(receipt);
    }

    private static void AddStayAmendmentOperation(
        ReservationsDbContext context,
        Reservation reservation,
        Guid operationId,
        Guid? inventoryRequestId,
        bool outcomeUnknown)
    {
        context.Reservations.Add(reservation);
        context.ManagementOperations.Add(new ReservationManagementOperation(
            new ReservationManagementOperationRecord(
                operationId,
                TenantId,
                reservation.PropertyId,
                reservation.Id,
                ReservationManagementOperationKind.StayAmendment,
                ExpectedVersion: null,
                reservation.DetailsRevision,
                BusinessDate: null,
                Now,
                Digest)));
        ReservationStayAmendmentOperation operation = outcomeUnknown
            ? ReservationStayAmendmentOperation.CreateOutcomeUnknown(
                operationId,
                TenantId,
                reservation.PropertyId,
                reservation.Id,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                Digest,
                reservation.DetailsRevision,
                Now).Value
            : ReservationStayAmendmentOperation.CreatePending(
                operationId,
                TenantId,
                reservation.PropertyId,
                reservation.Id,
                inventoryRequestId!.Value,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                Digest,
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
        context.StayAmendmentOperations.Add(operation);
    }

    private static WorkspaceTerminationFenceSnapshot FrozenFence() =>
        new(
            ProcessId,
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            Version: 3);

    private static TenantTerminationExportRequest Request() =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantId,
                ProcessId,
                CaseId,
                ApprovalRevision: 1,
                OperationRevision: 2,
                TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("60000000-0000-0000-0000-000000000001"),
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            WorkspaceFenceRevision: 3,
            Digest,
            FrozenAtUtc);

    private static TenantTerminationContributionRequest DestroyRequest() =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantId,
            ProcessId,
            CaseId,
            ApprovalRevision: 1,
            OperationRevision: 2,
            TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("60000000-0000-0000-0000-000000000002"),
            Guid.Parse("70000000-0000-0000-0000-000000000002"),
            Digest,
            "termination-executor",
            Now.AddHours(2));

    private static async Task<bool> HasOwnerRecordsAsync(
        ReservationsDbContext context) =>
        await context.Reservations.AnyAsync() ||
        await context.DataRightsCorrectionReceipts.AnyAsync() ||
        await context.ProcessingRestrictions.AnyAsync() ||
        await context.ProcessingRestrictionReceipts.AnyAsync() ||
        await context.ProcessingRestrictionProjections.AnyAsync() ||
        await context.DataHolds.AnyAsync() ||
        await context.DataHoldReceipts.AnyAsync() ||
        await context.AnonymisationReceipts.AnyAsync() ||
        await context.AnonymisationTombstones.AnyAsync() ||
        await context.AnonymisationRestoreReceipts.AnyAsync() ||
        await context.RetentionExecutions.AnyAsync() ||
        await context.RetentionAnonymisationReceipts.AnyAsync() ||
        await context.GuestRecordLinkProcesses.AnyAsync() ||
        await context.ReservationGuests.AnyAsync() ||
        await context.RequestedInventoryUnits.AnyAsync() ||
        await context.ReservationDetailsHistory.AnyAsync() ||
        await context.ArrivalReminders.AnyAsync() ||
        await context.ExternalOperations.AnyAsync() ||
        await context.StayAmendmentOperations.AnyAsync() ||
        await context.ManagementOperations.AnyAsync();

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static ReservationsDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new ReservationsDbContext(
            options,
            new TestScopeContext(),
            fences);
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

    private sealed class MutableFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public WorkspaceTerminationFenceSnapshot? Current { get; set; }

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Current);
        }
    }

    private sealed class ThrowingFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Fence store unavailable.");
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock(
        DateTimeOffset? utcNow = null) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow ?? Now;
    }
}
