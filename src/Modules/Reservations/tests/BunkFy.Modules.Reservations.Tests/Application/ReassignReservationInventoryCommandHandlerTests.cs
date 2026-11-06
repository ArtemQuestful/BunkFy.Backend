namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReassignReservationInventoryCommandHandlerTests
{
    [Fact]
    public async Task Staff_reassignment_stays_pending_until_inventory_confirms_it()
    {
        Guid currentUnitId = Guid.NewGuid();
        Guid targetUnitId = Guid.NewGuid();
        Reservation reservation = CreateConfirmedReservation(currentUnitId);
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations);
        Guid amendmentRequestId = Guid.NewGuid();

        Result<ReservationMutationReceiptDto> result = await handler.HandleAsync(
            new(
                reservation.PropertyId,
                reservation.Id,
                amendmentRequestId,
                [targetUnitId],
                reservation.DetailsRevision,
                "user:operator-a"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(reservation.DetailsRevision, result.Value.DetailsRevision);
        Assert.Equal(amendmentRequestId, reservation.PendingAllocationAmendmentId);
        Assert.Equal([currentUnitId], reservation.RequestedUnits.Select(unit => unit.InventoryUnitId));
        ReservationAllocationAmendmentRequestedDomainEvent domainEvent =
            Assert.IsType<ReservationAllocationAmendmentRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.Equal([targetUnitId], domainEvent.InventoryUnitIds);
        Assert.Equal(ReservationDetailsChangeOrigin.Staff, reservation.PendingDetailsChangeOrigin);
        ReservationManagementOperationRecord operation = Assert.Single(operations.Items);
        Assert.True(operation.MatchesInventoryAmendment(
            expectedDetailsRevision: 1,
            operation.RequestFingerprint!));
        Assert.Equal(1, projection.ValidationCount);
    }

    [Fact]
    public async Task Exact_pending_retry_does_not_revalidate_inventory_or_publish_again()
    {
        Guid targetUnitId = Guid.NewGuid();
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations,
            ids);
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            targetUnitId);

        Result<ReservationMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        reservation.ClearDomainEvents();
        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value.Version, replay.Value.Version);
        Assert.Equal(1, projection.ValidationCount);
        Assert.Equal(2, ids.Count);
        Assert.Single(operations.Items);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public async Task Legacy_pending_staff_retry_without_durable_evidence_fails_closed()
    {
        Guid targetUnitId = Guid.NewGuid();
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            targetUnitId);
        Assert.True(reservation.BeginAllocationAmendment(
            command.AmendmentRequestId,
            Guid.NewGuid(),
            Fingerprint(command),
            reservation.Arrival,
            reservation.Departure,
            command.InventoryUnitIds,
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            command.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Staff,
            command.ActorId,
            adapterConnectionId: null,
            externalOperationId: null,
            correlationId: command.AmendmentRequestId,
            eventId: Guid.NewGuid(),
            nowUtc: Now).IsSuccess);
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations,
            ids);

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(
            ReservationsApplicationErrors.ManagementOperationConflict,
            replay.Error);
        Assert.Empty(operations.Items);
        Assert.Equal(0, projection.ValidationCount);
        Assert.Equal(0, ids.Count);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public async Task Pending_adapter_amendment_is_never_backfilled_into_management_journal()
    {
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.True(reservation.BeginAllocationAmendment(
            command.AmendmentRequestId,
            Guid.NewGuid(),
            Fingerprint(command),
            reservation.Arrival,
            reservation.Departure,
            command.InventoryUnitIds,
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            command.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            "adapter:test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            command.AmendmentRequestId,
            Guid.NewGuid(),
            Now).IsSuccess);
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations);

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(
            ReservationsApplicationErrors.AllocationAmendmentInProgress,
            replay.Error);
        Assert.Empty(operations.Items);
        Assert.Equal(0, projection.ValidationCount);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public async Task Exact_confirmed_retry_returns_current_receipt_without_new_request()
    {
        Guid targetUnitId = Guid.NewGuid();
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations,
            ids);
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            targetUnitId);
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = reservation.PendingInventoryAmendmentRequestId!.Value;
        Assert.True(reservation.CompleteAllocationAmendment(
            inventoryRequestId,
            reservation.AllocationId!.Value,
            reservation.Arrival,
            reservation.Departure,
            [targetUnitId],
            allocationVersion: 2,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        reservation.ClearDomainEvents();

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(2, replay.Value.DetailsRevision);
        Assert.Equal([targetUnitId], reservation.RequestedUnits.Select(unit => unit.InventoryUnitId));
        Assert.Equal(1, projection.ValidationCount);
        Assert.Equal(2, ids.Count);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public async Task Exact_rejected_retry_does_not_restart_amendment()
    {
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations,
            ids);
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        Guid inventoryRequestId = reservation.PendingInventoryAmendmentRequestId!.Value;
        Assert.True(reservation.RejectAllocationAmendment(
            inventoryRequestId,
            reservation.AllocationId!.Value,
            rejectionCode: 2,
            Now.AddMinutes(1)).IsSuccess);

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Null(reservation.PendingAllocationAmendmentId);
        Assert.Equal(2, reservation.LastAllocationAmendmentRejectionCode);
        Assert.Equal(1, projection.ValidationCount);
        Assert.Equal(2, ids.Count);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Changed_reuse_of_amendment_request_id_conflicts_before_inventory_validation()
    {
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations);
        ReassignReservationInventoryCommand command = Command(
            reservation,
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        reservation.ClearDomainEvents();

        Result<ReservationMutationReceiptDto> changedUnits = await handler.HandleAsync(
            command with { InventoryUnitIds = [Guid.NewGuid()] },
            CancellationToken.None);
        Result<ReservationMutationReceiptDto> changedRevision = await handler.HandleAsync(
            command with { ExpectedDetailsRevision = command.ExpectedDetailsRevision + 1 },
            CancellationToken.None);

        Assert.Equal(ReservationsApplicationErrors.ManagementOperationConflict, changedUnits.Error);
        Assert.Equal(ReservationsApplicationErrors.ManagementOperationConflict, changedRevision.Error);
        Assert.Equal(1, projection.ValidationCount);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public async Task Unchanged_request_does_not_bind_amendment_request_id()
    {
        Guid currentUnitId = Guid.NewGuid();
        Reservation reservation = CreateConfirmedReservation(currentUnitId);
        reservation.ClearDomainEvents();
        FakeInventoryProjectionRepository projection = new();
        FakeReservationManagementOperationRepository operations = new();
        ReassignReservationInventoryCommandHandler handler = CreateHandler(
            reservation,
            projection,
            operations);
        Guid operationId = Guid.NewGuid();
        long originalVersion = reservation.Version;

        Result<ReservationMutationReceiptDto> unchanged = await handler.HandleAsync(
            Command(reservation, operationId, currentUnitId),
            CancellationToken.None);
        long versionAfterNoOp = reservation.Version;
        Result<ReservationMutationReceiptDto> changed = await handler.HandleAsync(
            Command(reservation, operationId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(unchanged.IsSuccess, unchanged.Error.Code);
        Assert.Equal(originalVersion, versionAfterNoOp);
        Assert.True(changed.IsSuccess, changed.Error.Code);
        Assert.Single(operations.Items);
        Assert.NotNull(reservation.PendingAllocationAmendmentId);
        Assert.Equal(2, projection.ValidationCount);
    }

    [Fact]
    public async Task Replay_reads_journal_after_reservation_lock_and_reload()
    {
        List<string> trace = [];
        Reservation reservation = CreateConfirmedReservation(Guid.NewGuid());
        Guid operationId = Guid.NewGuid();
        ReassignReservationInventoryCommand command = Command(
            reservation,
            operationId,
            Guid.NewGuid());
        string fingerprint = Fingerprint(command);
        FakeReservationManagementOperationRepository operations = new(trace);
        operations.Items.Add(new(
            operationId,
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.Id,
            ReservationManagementOperationKind.InventoryAmendment,
            ExpectedVersion: null,
            reservation.DetailsRevision,
            BusinessDate: null,
            Now,
            fingerprint));
        FakeReservationStayAmendmentOperationRepository stayOperations = new();
        stayOperations.Items.Add(
            ReservationStayAmendmentOperation.CreateOutcomeUnknown(
                operationId,
                reservation.ScopeId,
                reservation.PropertyId,
                reservation.Id,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                fingerprint,
                reservation.DetailsRevision,
                Now).Value);
        ReservationStayAmendmentCoordinator coordinator = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation, trace),
                new RecordingReservationOperationLock(trace),
                new TestScopeContext()),
            new FakeInventoryProjectionRepository(),
            operations,
            stayOperations,
            new TestClock(),
            new TestIdGenerator());
        ReassignReservationInventoryCommandHandler handler = new(coordinator);

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(["lock", "reservation", "journal"], trace);
    }

    private static ReassignReservationInventoryCommandHandler CreateHandler(
        Reservation reservation,
        IInventoryProjectionRepository projection,
        IReservationManagementOperationRepository operations,
        IIdGenerator? ids = null)
    {
        ReservationStayAmendmentCoordinator coordinator = new(
            ReservationMutationTestSupport.Create(new FakeReservationRepository(reservation)),
            projection,
            operations,
            new FakeReservationStayAmendmentOperationRepository(),
            new TestClock(),
            ids ?? new TestIdGenerator());
        return new ReassignReservationInventoryCommandHandler(coordinator);
    }

    private static ReassignReservationInventoryCommand Command(
        Reservation reservation,
        Guid operationId,
        Guid targetUnitId) => new(
            reservation.PropertyId,
            reservation.Id,
            operationId,
            [targetUnitId],
            reservation.DetailsRevision,
            "user:operator-a");

    private static string Fingerprint(ReassignReservationInventoryCommand command)
    {
        string canonical = string.Join(
            '|',
            command.ReservationId.ToString("N"),
            command.AmendmentRequestId.ToString("N"),
            command.ExpectedDetailsRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', command.InventoryUnitIds.Order().Select(id => id.ToString("N"))));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static Reservation CreateConfirmedReservation(Guid inventoryUnitId)
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [inventoryUnitId],
            "Ada Guest",
            "ada@example.test",
            null,
            1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now).Value;
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now).IsSuccess);
        return reservation;
    }

    private sealed class FakeReservationRepository(
        Reservation reservation,
        List<string>? trace = null) : IReservationRepository
    {
        public Task AddAsync(Reservation value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Reservation?> GetAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken)
        {
            trace?.Add("reservation");
            return Task.FromResult<Reservation?>(
                reservation.PropertyId == propertyId && reservation.Id == reservationId ? reservation : null);
        }

        public Task<Reservation?> GetForDataRightsAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(propertyId, reservationId, cancellationToken);

        public Task<Reservation?> GetAsyncByReservationId(Guid reservationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetByExternalSourceAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ExternalSourceExistsAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReservationListResponse> ListAsync(
            Guid propertyId,
            IReadOnlyCollection<ReservationStatus>? statuses,
            string? search,
            ReservationListOrder order,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeInventoryProjectionRepository : IInventoryProjectionRepository
    {
        public int ValidationCount { get; private set; }

        public Task<InventoryUnitSelectionValidation> ValidateSelectionAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken)
        {
            this.ValidationCount++;
            return Task.FromResult(InventoryUnitSelectionValidation.Valid);
        }

        public Task ApplyUnitAsync(ReservationInventoryUnitWriteModel unit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyBlockAsync(ReservationInventoryBlockWriteModel block, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseBlockAsync(
            string scopeId,
            Guid propertyId,
            Guid inventoryUnitId,
            Guid blockId,
            long version,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyAllocationAsync(
            ReservationInventoryAllocationWriteModel allocation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseAllocationAsync(
            string scopeId,
            Guid allocationId,
            Guid reservationId,
            long version,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class CountingIdGenerator : IIdGenerator
    {
        public int Count { get; private set; }

        public Guid NewId()
        {
            this.Count++;
            return Guid.NewGuid();
        }
    }

    private sealed class FakeReservationManagementOperationRepository(
        List<string>? trace = null) : IReservationManagementOperationRepository
    {
        public List<ReservationManagementOperationRecord> Items { get; } = [];

        public Task<ReservationManagementOperationRecord?> GetAsync(
            Guid reservationId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            trace?.Add("journal");
            return Task.FromResult(this.Items.SingleOrDefault(item =>
                item.ReservationId == reservationId &&
                item.OperationId == operationId));
        }

        public Task AddAsync(
            ReservationManagementOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Items.Add(operation);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReservationStayAmendmentOperationRepository
        : IReservationStayAmendmentOperationRepository
    {
        public List<ReservationStayAmendmentOperation> Items { get; } = [];

        public Task<ReservationStayAmendmentOperation?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
                this.Items.SingleOrDefault(item =>
                    item.PropertyId == propertyId &&
                    item.ReservationId == reservationId &&
                    item.Id == operationId));

        public Task<ReservationStayAmendmentOperation?> GetVisibleAsync(
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(
                propertyId,
                reservationId,
                operationId,
                cancellationToken);

        public Task<ReservationStayAmendmentOperation?> GetByInventoryRequestIdAsync(
            Guid inventoryRequestId,
            CancellationToken cancellationToken) => Task.FromResult(
                this.Items.SingleOrDefault(item =>
                    item.InventoryRequestId == inventoryRequestId));

        public Task AddAsync(
            ReservationStayAmendmentOperation operation,
            CancellationToken cancellationToken)
        {
            this.Items.Add(operation);
            return Task.CompletedTask;
        }

        public Task<ReservationStayAmendmentRecoveryPageRecord> ListRecoveryAsync(
            Guid propertyId,
            ReservationStayAmendmentRecoveryCursorRecord? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingReservationOperationLock(List<string> trace)
        : IReservationOperationLock
    {
        public Task<bool> TryAcquireExistingAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            trace.Add("lock");
            return Task.FromResult(true);
        }

        public Task AcquireCoordinateAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
}
