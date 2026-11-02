namespace BunkFy.Modules.Reservations.Tests.Persistence;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Application.Events;
using Gma.Framework.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationsInboxDomainEventDispatchTests
{
    private const string ScopeId = "tenant-a";
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Allocation_confirmation_dispatches_and_persists_each_outbox_event_once()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Reservation reservation = await SeedPendingReservationAsync(dbContext);
        TestClock clock = new();
        TestIdGenerator ids = new();
        ReservationsOutboxWriter outbox = CreateOutboxWriter(dbContext, clock);
        TestOutboxWriterRegistry outboxWriters = new(outbox);
        ProjectingDomainEventDispatcher dispatcher = new(
            new ReservationGuestStayChangedOutboxProjector(outboxWriters, ids));
        InventoryAllocationConfirmedHandler handler = CreateHandler(
            reservation,
            outboxWriters,
            dispatcher,
            clock,
            ids);
        ReservationsInboxStore store = new(dbContext, clock, ids, dispatcher);
        InventoryAllocationConfirmedIntegrationEvent outcome = CreateOutcome(reservation);

        InboxProcessResult result = await store.ProcessAsync(
            CreateMessage(outcome),
            cancellationToken => handler.HandleAsync(outcome, cancellationToken),
            CancellationToken.None);

        Assert.Equal(InboxProcessStatus.Processed, result.Status);
        Assert.Equal(ReservationState.Confirmed, reservation.Status);
        Assert.Empty(reservation.DomainEvents);
        Assert.Single(dispatcher.DispatchedEvents);
        Assert.IsType<ReservationGuestStayChangedDomainEvent>(dispatcher.DispatchedEvents[0]);
        Assert.Equal(1, dispatcher.ProjectedGuestStayChangeCount);

        OutboxMessage[] persistedOutbox = await dbContext.OutboxMessages
            .AsNoTracking()
            .ToArrayAsync();
        Assert.Equal(2, persistedOutbox.Length);
        Assert.Single(
            persistedOutbox,
            message => message.EventType == typeof(ReservationGuestStayChangedIntegrationEvent).FullName);
        Assert.Single(
            persistedOutbox,
            message => message.EventType == typeof(ReservationConfirmedIntegrationEvent).FullName);
        Assert.Equal(
            InboxMessageStatus.Processed,
            (await dbContext.InboxMessages.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Allocation_confirmation_dispatch_failure_retains_events_and_rolls_back_mutation()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Reservation reservation = await SeedPendingReservationAsync(dbContext);
        TestClock clock = new();
        TestIdGenerator ids = new();
        ReservationsOutboxWriter outbox = CreateOutboxWriter(dbContext, clock);
        TestOutboxWriterRegistry outboxWriters = new(outbox);
        ThrowingDomainEventDispatcher dispatcher = new();
        InventoryAllocationConfirmedHandler handler = CreateHandler(
            reservation,
            outboxWriters,
            dispatcher,
            clock,
            ids);
        ReservationsInboxStore store = new(dbContext, clock, ids, dispatcher);
        InventoryAllocationConfirmedIntegrationEvent outcome = CreateOutcome(reservation);

        InboxProcessResult result = await store.ProcessAsync(
            CreateMessage(outcome),
            cancellationToken => handler.HandleAsync(outcome, cancellationToken),
            CancellationToken.None);

        Assert.Equal(InboxProcessStatus.Failed, result.Status);
        Assert.Contains("inbox-handler-failed:InvalidOperationException", result.Error);
        Assert.Single(dispatcher.DispatchedEvents);
        Assert.IsType<ReservationGuestStayChangedDomainEvent>(
            Assert.Single(reservation.DomainEvents));
        Assert.Equal(
            ReservationState.PendingAllocation,
            (await dbContext.Reservations.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await dbContext.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            InboxMessageStatus.Failed,
            (await dbContext.InboxMessages.AsNoTracking().SingleAsync()).Status);
    }

    private static InventoryAllocationConfirmedHandler CreateHandler(
        Reservation reservation,
        IOutboxWriterRegistry outboxWriters,
        IDomainEventDispatcher dispatcher,
        ISystemClock clock,
        IIdGenerator ids) => new(
            ReservationMutationTestSupport.Create(
                new TestReservationRepository(reservation),
                scopeContext: new TestScopeContext()),
            new RecordingInventoryProjection(),
            outboxWriters,
            new ReservationInboxDomainEventDispatcher(dispatcher),
            clock,
            ids);

    private static ReservationsDbContext CreateDbContext()
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
        return new ReservationsDbContext(options, new TestScopeContext());
    }

    private static async Task<Reservation> SeedPendingReservationAsync(
        ReservationsDbContext dbContext)
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            ScopeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 12),
            new DateOnly(2026, 8, 14),
            [Guid.NewGuid()],
            "Ada Guest",
            "ada@example.test",
            phone: null,
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: "user:owner-a",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now).Value;
        Assert.True(reservation.LinkGuest(
            Guid.NewGuid(),
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            reservation.Version,
            "user:owner-a",
            Guid.NewGuid(),
            Now).IsSuccess);
        reservation.ClearDomainEvents();
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        return reservation;
    }

    private static InventoryAllocationConfirmedIntegrationEvent CreateOutcome(
        Reservation reservation) => new(
            Guid.NewGuid(),
            ScopeId,
            Now,
            Guid.NewGuid(),
            reservation.Id,
            reservation.AllocationRequestId,
            reservation.PropertyId,
            reservation.Arrival,
            reservation.Departure,
            reservation.RequestedUnits.Select(unit => unit.InventoryUnitId).ToArray(),
            allocationVersion: 1);

    private static InboxMessageRecord CreateMessage(
        InventoryAllocationConfirmedIntegrationEvent outcome) => new(
            outcome.EventId,
            ReservationsModuleMetadata.AllocationConfirmedHandlerName,
            InventoryIntegrationSubjects.CreateAllocationConfirmed(),
            InventoryAllocationConfirmedIntegrationEvent.EventType,
            InventoryAllocationConfirmedIntegrationEvent.EventVersion,
            scopeId: null,
            outcome.OccurredAtUtc);

    private static ReservationsOutboxWriter CreateOutboxWriter(
        ReservationsDbContext dbContext,
        ISystemClock clock) => new(
            dbContext,
            clock,
            Options.Create(new ApplicationIdentityOptions { Namespace = "bunkfy-test" }),
            [new TestScopeResolver()]);

    private sealed class TestReservationRepository(Reservation reservation)
        : IReservationRepository
    {
        public Task AddAsync(Reservation value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                reservation.PropertyId == propertyId && reservation.Id == reservationId
                    ? reservation
                    : null);

        public Task<Reservation?> GetForDataRightsAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(propertyId, reservationId, cancellationToken);

        public Task<Reservation?> GetAsyncByReservationId(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(reservation.Id == reservationId ? reservation : null);

        public Task<Reservation?> GetByExternalSourceAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) =>
            Task.FromResult<Reservation?>(null);

        public Task<bool> ExternalSourceExistsAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<ReservationListResponse> ListAsync(
            Guid propertyId,
            IReadOnlyCollection<ReservationStatus>? statuses,
            string? search,
            ReservationListOrder order,
            Gma.Framework.Pagination.PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingInventoryProjection : IInventoryProjectionRepository
    {
        public Task ApplyAllocationAsync(
            ReservationInventoryAllocationWriteModel allocation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<InventoryUnitSelectionValidation> ValidateSelectionAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyUnitAsync(
            ReservationInventoryUnitWriteModel unit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyBlockAsync(
            ReservationInventoryBlockWriteModel block,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseBlockAsync(
            string scopeId,
            Guid propertyId,
            Guid inventoryUnitId,
            Guid blockId,
            long version,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseAllocationAsync(
            string scopeId,
            Guid allocationId,
            Guid reservationId,
            long version,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestOutboxWriterRegistry(IOutboxWriter writer)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(ReservationsModuleMetadata.Name, moduleName);
            return writer;
        }
    }

    private sealed class ProjectingDomainEventDispatcher(
        ReservationGuestStayChangedOutboxProjector projector)
        : IDomainEventDispatcher
    {
        public List<IDomainEvent> DispatchedEvents { get; } = [];
        public int ProjectedGuestStayChangeCount { get; private set; }

        public async Task DispatchAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken)
        {
            this.DispatchedEvents.AddRange(domainEvents);
            foreach (ReservationGuestStayChangedDomainEvent domainEvent in
                     domainEvents.OfType<ReservationGuestStayChangedDomainEvent>())
            {
                await projector.HandleAsync(domainEvent, cancellationToken);
                this.ProjectedGuestStayChangeCount++;
            }
        }
    }

    private sealed class ThrowingDomainEventDispatcher : IDomainEventDispatcher
    {
        public List<IDomainEvent> DispatchedEvents { get; } = [];

        public Task DispatchAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken)
        {
            this.DispatchedEvents.AddRange(domainEvents);
            throw new InvalidOperationException("Domain event dispatch failed.");
        }
    }

    private sealed class TestScopeResolver : IIntegrationEventScopeResolver
    {
        public string? ResolveScopeId(IIntegrationEvent integrationEvent) =>
            integrationEvent is IScopedIntegrationEvent scoped
                ? scoped.ScopeId
                : null;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => ReservationsInboxDomainEventDispatchTests.ScopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
