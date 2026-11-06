namespace BunkFy.Modules.Reservations.Tests;

using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayLifecycleTests
{
    [Fact]
    public async Task Check_in_handler_preserves_business_date_and_actor_provenance()
    {
        Reservation reservation = CreateConfirmedReservation();
        FakeReservationRepository repository = new(reservation);
        FakeReservationManagementOperationRepository operations = new();
        CheckInReservationCommandHandler handler = new(
            CreateLifecycle(repository, operations),
            new TestIdGenerator());

        Result<ReservationMutationReceiptDto> result = await handler.HandleAsync(
            new CheckInReservationCommand(
                Guid.NewGuid(),
                reservation.PropertyId,
                reservation.Id,
                new DateOnly(2026, 8, 1),
                reservation.Version,
                "  user:operator-a  "),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ReservationStatus.CheckedIn, result.Value.Status);
        Assert.Equal(new DateOnly(2026, 8, 1), reservation.CheckedInBusinessDate);
        Assert.Equal("user:operator-a", reservation.CheckedInBy);
        Assert.Equal(TestClock.Now, reservation.CheckedInAtUtc);
    }

    [Fact]
    public async Task Exact_checkout_replay_returns_current_state_after_release_rejection()
    {
        Reservation reservation = CreateCheckedInReservation();
        FakeReservationRepository repository = new(reservation);
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        CheckOutReservationCommandHandler handler = new(
            CreateLifecycle(repository, operations),
            ids);
        Guid operationId = Guid.NewGuid();
        long expectedVersion = reservation.Version;
        CheckOutReservationCommand command = new(
            operationId,
            reservation.PropertyId,
            reservation.Id,
            reservation.Departure,
            expectedVersion,
            "user:operator-a");

        Result<ReservationMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(ReservationStatus.CheckoutPending, first.Value.Status);
        Assert.Equal(2, ids.Count);
        Assert.Single(operations.Items);
        Assert.True(reservation.RestoreAfterReleaseRejection(
            reservation.ReleaseRequestId!.Value,
            rejectionCode: 17,
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1)).IsSuccess);

        Result<ReservationMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(ReservationStatus.CheckedIn, replay.Value.Status);
        Assert.Equal(reservation.Version, replay.Value.Version);
        Assert.Equal(2, ids.Count);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Reusing_operation_id_for_changed_lifecycle_request_conflicts()
    {
        Reservation reservation = CreateConfirmedReservation();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReservationManagementLifecycleCoordinator lifecycle = CreateLifecycle(
            new FakeReservationRepository(reservation),
            operations);
        Guid operationId = Guid.NewGuid();
        long expectedVersion = reservation.Version;
        CheckInReservationCommandHandler checkIn = new(lifecycle, ids);
        MarkReservationNoShowCommandHandler noShow = new(lifecycle, ids);

        Result<ReservationMutationReceiptDto> first = await checkIn.HandleAsync(
            new(
                operationId,
                reservation.PropertyId,
                reservation.Id,
                reservation.Arrival,
                expectedVersion,
                "user:operator-a"),
            CancellationToken.None);
        Result<ReservationMutationReceiptDto> conflict = await noShow.HandleAsync(
            new(
                operationId,
                reservation.PropertyId,
                reservation.Id,
                reservation.Arrival,
                expectedVersion,
                "user:operator-a"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(conflict.IsFailure);
        Assert.Equal(
            ReservationsApplicationErrors.ManagementOperationConflict,
            conflict.Error);
        Assert.Equal(1, ids.Count);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Failed_lifecycle_request_does_not_bind_operation_id()
    {
        Reservation reservation = CreateConfirmedReservation();
        FakeReservationManagementOperationRepository operations = new();
        CountingIdGenerator ids = new();
        ReservationManagementLifecycleCoordinator lifecycle = CreateLifecycle(
            new FakeReservationRepository(reservation),
            operations);
        Guid operationId = Guid.NewGuid();
        long expectedVersion = reservation.Version;
        CheckOutReservationCommandHandler checkOut = new(lifecycle, ids);
        CheckInReservationCommandHandler checkIn = new(lifecycle, ids);

        Result<ReservationMutationReceiptDto> failed = await checkOut.HandleAsync(
            new(
                operationId,
                reservation.PropertyId,
                reservation.Id,
                reservation.Departure,
                expectedVersion,
                "user:operator-a"),
            CancellationToken.None);
        Result<ReservationMutationReceiptDto> reused = await checkIn.HandleAsync(
            new(
                operationId,
                reservation.PropertyId,
                reservation.Id,
                reservation.Arrival,
                expectedVersion,
                "user:operator-a"),
            CancellationToken.None);

        Assert.True(failed.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.InvalidTransition, failed.Error);
        Assert.True(reused.IsSuccess, reused.Error.Code);
        Assert.Equal(ReservationStatus.CheckedIn, reused.Value.Status);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Lifecycle_journal_is_read_only_after_reservation_lock_and_reload()
    {
        List<string> trace = [];
        Reservation reservation = CreateConfirmedReservation();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = reservation.Version;
        FakeReservationManagementOperationRepository operations = new(trace);
        operations.Items.Add(new(
            operationId,
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.Id,
            ReservationManagementOperationKind.CheckIn,
            expectedVersion,
            ExpectedDetailsRevision: null,
            reservation.Arrival,
            TestClock.Now));
        ReservationManagementLifecycleCoordinator lifecycle = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation, trace),
                new RecordingReservationOperationLock(trace),
                new TestReservationScopeContext()),
            operations,
            new TestClock());

        Result<ReservationMutationReceiptDto> replay = await lifecycle.ExecuteAsync(
            operationId,
            reservation.PropertyId,
            reservation.Id,
            ReservationManagementOperationKind.CheckIn,
            expectedVersion,
            reservation.Arrival,
            "user:operator-a",
            (_, _) => throw new InvalidOperationException("Replay must not apply."),
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(["lock", "reservation", "journal"], trace);
    }

    [Fact]
    public async Task Stay_projectors_publish_correlated_release_and_terminal_events()
    {
        RecordingOutbox outbox = new();
        RecordingOutboxRegistry registry = new(outbox);
        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid allocationId = Guid.NewGuid();
        Guid releaseRequestId = Guid.NewGuid();
        DateOnly businessDate = new(2026, 8, 1);

        await new ReservationCheckedInOutboxProjector(registry).HandleAsync(
            new(Guid.NewGuid(), TestClock.Now, "tenant-a", reservationId, propertyId,
                businessDate, "user:operator-a", 3),
            CancellationToken.None);
        await new ReservationNoShowRequestedOutboxProjector(registry).HandleAsync(
            new(Guid.NewGuid(), TestClock.Now, "tenant-a", reservationId, propertyId,
                allocationId, releaseRequestId, 2),
            CancellationToken.None);
        await new ReservationNoShowOutboxProjector(registry).HandleAsync(
            new(Guid.NewGuid(), TestClock.Now, "tenant-a", reservationId, propertyId,
                businessDate, "user:operator-a", 4),
            CancellationToken.None);
        await new ReservationCheckoutRequestedOutboxProjector(registry).HandleAsync(
            new(Guid.NewGuid(), TestClock.Now, "tenant-a", reservationId, propertyId,
                allocationId, releaseRequestId, 2),
            CancellationToken.None);
        await new ReservationCheckedOutOutboxProjector(registry).HandleAsync(
            new(Guid.NewGuid(), TestClock.Now, "tenant-a", reservationId, propertyId,
                businessDate, "user:operator-a", 5),
            CancellationToken.None);

        Assert.Collection(
            outbox.Events,
            item => Assert.IsType<ReservationCheckedInIntegrationEvent>(item),
            item => Assert.Equal(
                releaseRequestId,
                Assert.IsType<InventoryAllocationReleaseRequestedIntegrationEvent>(item).ReleaseRequestId),
            item => Assert.IsType<ReservationNoShowIntegrationEvent>(item),
            item => Assert.Equal(
                releaseRequestId,
                Assert.IsType<InventoryAllocationReleaseRequestedIntegrationEvent>(item).ReleaseRequestId),
            item => Assert.IsType<ReservationCheckedOutIntegrationEvent>(item));
    }

    [Fact]
    public async Task Release_consumer_fences_projection_and_allows_exact_duplicate_repair()
    {
        Reservation reservation = CreateConfirmedReservation();
        RecordingInventoryProjection projection = new();
        InventoryAllocationReleasedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new ReservationInboxDomainEventDispatcher(new NoOpDomainEventDispatcher()),
            new TestClock(),
            new TestIdGenerator());

        await handler.HandleAsync(
            new(
                Guid.NewGuid(),
                reservation.ScopeId,
                TestClock.Now,
                reservation.AllocationId!.Value,
                reservation.Id,
                Guid.NewGuid(),
                reservation.AllocationVersion!.Value),
            CancellationToken.None);

        Assert.Equal(ReservationState.Confirmed, reservation.Status);
        Assert.Equal(0, projection.ReleaseCount);

        Guid releaseRequestId = Guid.NewGuid();
        Assert.True(reservation.RequestNoShow(
            reservation.Version,
            reservation.Arrival,
            "user:operator-a",
            releaseRequestId,
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        InventoryAllocationReleasedIntegrationEvent released = new(
            Guid.NewGuid(),
            reservation.ScopeId,
            TestClock.Now,
            reservation.AllocationId.Value,
            reservation.Id,
            releaseRequestId,
            reservation.AllocationVersion.Value);

        await handler.HandleAsync(released, CancellationToken.None);
        long terminalVersion = reservation.Version;
        await handler.HandleAsync(released, CancellationToken.None);

        Assert.Equal(ReservationState.NoShow, reservation.Status);
        Assert.Equal(terminalVersion, reservation.Version);
        Assert.Equal(2, projection.ReleaseCount);
    }

    [Fact]
    public async Task Stale_confirmation_cannot_reactivate_projection_after_rejection()
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
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
            TestClock.Now).Value;
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        RecordingInventoryProjection projection = new();
        InventoryAllocationConfirmedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new RecordingOutboxRegistry(new RecordingOutbox()),
            new ReservationInboxDomainEventDispatcher(
                new NoOpDomainEventDispatcher()),
            new TestClock(),
            new TestIdGenerator());

        await handler.HandleAsync(
            new InventoryAllocationConfirmedIntegrationEvent(
                Guid.NewGuid(),
                reservation.ScopeId,
                TestClock.Now,
                Guid.NewGuid(),
                reservation.Id,
                reservation.AllocationRequestId,
                reservation.PropertyId,
                reservation.Arrival,
                reservation.Departure,
                reservation.RequestedUnits
                    .Select(unit => unit.InventoryUnitId)
                    .ToArray(),
                allocationVersion: 1),
            CancellationToken.None);

        Assert.Equal(ReservationState.AllocationRejected, reservation.Status);
        Assert.Equal(0, projection.ApplyCount);
    }

    [Fact]
    public async Task Allocation_confirmation_rejects_mismatched_units_before_mutation()
    {
        Reservation reservation = CreatePendingReservation(Guid.NewGuid(), Guid.NewGuid());
        RecordingInventoryProjection projection = new();
        RecordingOutbox outbox = new();
        CountingIdGenerator ids = new();
        InventoryAllocationConfirmedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new RecordingOutboxRegistry(outbox),
            new ReservationInboxDomainEventDispatcher(
                new NoOpDomainEventDispatcher()),
            new TestClock(),
            ids);
        long originalVersion = reservation.Version;

        ReservationAllocationUnitMismatchException failure =
            await Assert.ThrowsAsync<ReservationAllocationUnitMismatchException>(
            () => handler.HandleAsync(
                CreateConfirmation(
                    reservation,
                    [reservation.RequestedUnits.First().InventoryUnitId, Guid.NewGuid()]),
                CancellationToken.None));

        Assert.Equal(
            ReservationsApplicationErrors.AllocationUnitMismatch.Code,
            failure.Message);
        Assert.Equal(ReservationState.PendingAllocation, reservation.Status);
        Assert.Equal(originalVersion, reservation.Version);
        Assert.Null(reservation.AllocationId);
        Assert.Equal(0, projection.ApplyCount);
        Assert.Empty(outbox.Events);
        Assert.Equal(0, ids.Count);
    }

    [Fact]
    public async Task Late_confirmation_rejects_mismatched_units_during_preallocation_cancellation()
    {
        Reservation reservation = CreatePendingReservation(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(reservation.RequestCancellation(
            reservation.Version,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        reservation.ClearDomainEvents();
        RecordingInventoryProjection projection = new();
        RecordingOutbox outbox = new();
        RecordingDomainEventDispatcher domainEvents = new();
        CountingIdGenerator ids = new();
        InventoryAllocationConfirmedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new RecordingOutboxRegistry(outbox),
            new ReservationInboxDomainEventDispatcher(domainEvents),
            new TestClock(),
            ids);
        long originalVersion = reservation.Version;

        ReservationAllocationUnitMismatchException failure =
            await Assert.ThrowsAsync<ReservationAllocationUnitMismatchException>(
            () => handler.HandleAsync(
                CreateConfirmation(
                    reservation,
                    [reservation.RequestedUnits.First().InventoryUnitId, Guid.NewGuid()]),
                CancellationToken.None));

        Assert.Equal(
            ReservationsApplicationErrors.AllocationUnitMismatch.Code,
            failure.Message);
        Assert.Equal(ReservationState.CancellationPending, reservation.Status);
        Assert.Equal(originalVersion, reservation.Version);
        Assert.Null(reservation.AllocationId);
        Assert.Empty(reservation.DomainEvents);
        Assert.Equal(0, projection.ApplyCount);
        Assert.Empty(outbox.Events);
        Assert.Equal(0, domainEvents.DispatchCount);
        Assert.Equal(0, ids.Count);
    }

    [Fact]
    public async Task Reordered_exact_confirmation_converges_and_replays_idempotently()
    {
        Guid firstUnitId = Guid.NewGuid();
        Guid secondUnitId = Guid.NewGuid();
        Reservation reservation = CreatePendingReservation(firstUnitId, secondUnitId);
        RecordingInventoryProjection projection = new();
        RecordingOutbox outbox = new();
        InventoryAllocationConfirmedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new RecordingOutboxRegistry(outbox),
            new ReservationInboxDomainEventDispatcher(
                new NoOpDomainEventDispatcher()),
            new TestClock(),
            new TestIdGenerator());
        InventoryAllocationConfirmedIntegrationEvent confirmation = CreateConfirmation(
            reservation,
            [secondUnitId, firstUnitId]);

        await handler.HandleAsync(confirmation, CancellationToken.None);
        long confirmedVersion = reservation.Version;
        await handler.HandleAsync(confirmation, CancellationToken.None);

        Assert.Equal(ReservationState.Confirmed, reservation.Status);
        Assert.Equal(confirmation.AllocationId, reservation.AllocationId);
        Assert.Equal(confirmedVersion, reservation.Version);
        Assert.Equal(2, projection.ApplyCount);
        Assert.Single(outbox.Events);
    }

    [Fact]
    public async Task Original_confirmation_replay_after_amendment_preserves_current_units()
    {
        Guid originalUnitId = Guid.NewGuid();
        Guid amendedUnitId = Guid.NewGuid();
        Reservation reservation = CreatePendingReservation(originalUnitId);
        RecordingInventoryProjection projection = new();
        RecordingOutbox outbox = new();
        InventoryAllocationConfirmedHandler handler = new(
            ReservationMutationTestSupport.Create(
                new FakeReservationRepository(reservation)),
            projection,
            new RecordingOutboxRegistry(outbox),
            new ReservationInboxDomainEventDispatcher(
                new NoOpDomainEventDispatcher()),
            new TestClock(),
            new TestIdGenerator());
        InventoryAllocationConfirmedIntegrationEvent originalConfirmation =
            CreateConfirmation(reservation, [originalUnitId]);
        await handler.HandleAsync(originalConfirmation, CancellationToken.None);
        Guid amendmentInventoryRequestId = Guid.NewGuid();
        Assert.True(reservation.BeginAllocationAmendment(
            Guid.NewGuid(),
            amendmentInventoryRequestId,
            new string('a', Reservation.RequestFingerprintLength),
            reservation.Arrival,
            reservation.Departure,
            [amendedUnitId],
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            reservation.DetailsRevision,
            ReservationDetailsChangeOrigin.Staff,
            "user:operator-a",
            adapterConnectionId: null,
            externalOperationId: null,
            correlationId: Guid.NewGuid(),
            eventId: Guid.NewGuid(),
            nowUtc: TestClock.Now.AddMinutes(1)).IsSuccess);
        Assert.True(reservation.CompleteAllocationAmendment(
            amendmentInventoryRequestId,
            originalConfirmation.AllocationId,
            reservation.Arrival,
            reservation.Departure,
            [amendedUnitId],
            allocationVersion: 2,
            detailsEventId: Guid.NewGuid(),
            nowUtc: TestClock.Now.AddMinutes(2)).IsSuccess);
        long amendedVersion = reservation.Version;

        await handler.HandleAsync(originalConfirmation, CancellationToken.None);

        Assert.Equal(amendedVersion, reservation.Version);
        Assert.Equal(
            [amendedUnitId],
            reservation.RequestedUnits.Select(unit => unit.InventoryUnitId));
        Assert.Equal(2L, reservation.AllocationVersion);
        Assert.Equal(2, projection.ApplyCount);
        Assert.Single(outbox.Events);
    }

    private static InventoryAllocationConfirmedIntegrationEvent CreateConfirmation(
        Reservation reservation,
        IReadOnlyCollection<Guid> inventoryUnitIds) => new(
        Guid.NewGuid(),
        reservation.ScopeId,
        TestClock.Now,
        Guid.NewGuid(),
        reservation.Id,
        reservation.AllocationRequestId,
        reservation.PropertyId,
        reservation.Arrival,
        reservation.Departure,
        inventoryUnitIds,
        allocationVersion: 1);

    private static Reservation CreatePendingReservation(params Guid[] inventoryUnitIds) =>
        Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            inventoryUnitIds,
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
            TestClock.Now).Value;

    private static Reservation CreateConfirmedReservation()
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
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
            TestClock.Now).Value;
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        return reservation;
    }

    private static Reservation CreateCheckedInReservation()
    {
        Reservation reservation = CreateConfirmedReservation();
        Assert.True(reservation.CheckIn(
            reservation.Version,
            reservation.Arrival,
            "user:operator-a",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        return reservation;
    }

    private static ReservationManagementLifecycleCoordinator CreateLifecycle(
        IReservationRepository reservations,
        IReservationManagementOperationRepository operations) => new(
        ReservationMutationTestSupport.Create(reservations),
        operations,
        new TestClock());

    private sealed class FakeReservationRepository(
        Reservation reservation,
        List<string>? trace = null) : IReservationRepository
    {
        public Task AddAsync(Reservation value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Reservation?> GetAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken) =>
            this.GetOperationalAsync(propertyId, reservationId);

        private Task<Reservation?> GetOperationalAsync(
            Guid propertyId,
            Guid reservationId)
        {
            trace?.Add("reservation");
            return Task.FromResult<Reservation?>(
                reservation.PropertyId == propertyId && reservation.Id == reservationId
                    ? reservation
                    : null);
        }

        public Task<Reservation?> GetForDataRightsAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(propertyId, reservationId, cancellationToken);

        public Task<Reservation?> GetAsyncByReservationId(Guid reservationId, CancellationToken cancellationToken) =>
            Task.FromResult<Reservation?>(reservation.Id == reservationId ? reservation : null);

        public Task<Reservation?> GetByExternalSourceAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) => Task.FromResult<Reservation?>(null);

        public Task<bool> ExternalSourceExistsAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ReservationListResponse> ListAsync(
            Guid propertyId,
            IReadOnlyCollection<ReservationStatus>? statuses,
            string? search,
            ReservationListOrder order,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReservationListResponse([], pageRequest.Page, pageRequest.PageSize, false));
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => ReservationsModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox) : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
    }

    private sealed class RecordingInventoryProjection : IInventoryProjectionRepository
    {
        public int ApplyCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<InventoryUnitSelectionValidation> ValidateSelectionAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => Task.FromResult(InventoryUnitSelectionValidation.Valid);

        public Task ApplyUnitAsync(ReservationInventoryUnitWriteModel unit, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ApplyBlockAsync(ReservationInventoryBlockWriteModel block, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReleaseBlockAsync(
            string scopeId,
            Guid propertyId,
            Guid inventoryUnitId,
            Guid blockId,
            long version,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ApplyAllocationAsync(
            ReservationInventoryAllocationWriteModel allocation,
            CancellationToken cancellationToken)
        {
            this.ApplyCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseAllocationAsync(
            string scopeId,
            Guid allocationId,
            Guid reservationId,
            long version,
            CancellationToken cancellationToken)
        {
            this.ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(
            IReadOnlyCollection<Gma.Framework.Domain.IDomainEvent> domainEvents,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDomainEventDispatcher : IDomainEventDispatcher
    {
        public int DispatchCount { get; private set; }

        public Task DispatchAsync(
            IReadOnlyCollection<Gma.Framework.Domain.IDomainEvent> domainEvents,
            CancellationToken cancellationToken)
        {
            this.DispatchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
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

    private sealed class TestReservationScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
