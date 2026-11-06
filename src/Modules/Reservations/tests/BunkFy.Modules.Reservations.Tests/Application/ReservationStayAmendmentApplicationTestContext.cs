namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class ReservationStayAmendmentApplicationTestContext
{
    public ReservationStayAmendmentApplicationTestContext(
        IReadOnlyCollection<Guid> currentUnitIds,
        TimeOnly? expectedArrivalTime = null,
        TimeOnly? expectedDepartureTime = null)
    {
        this.Reservation = CreateConfirmedReservation(
            currentUnitIds,
            expectedArrivalTime,
            expectedDepartureTime);
        this.Reservation.ClearDomainEvents();
        this.Reservations = new FakeReservationRepository(this.Reservation);
        this.Mutations = ReservationMutationTestSupport.Create(this.Reservations);
        this.Coordinator = new ReservationStayAmendmentCoordinator(
            this.Mutations,
            this.Inventory,
            this.ManagementOperations,
            this.StayOperations,
            this.Clock,
            this.Ids);
    }

    public Reservation Reservation { get; }
    public FakeReservationRepository Reservations { get; }
    public FakeInventoryProjectionRepository Inventory { get; } = new();
    public FakeManagementOperationRepository ManagementOperations { get; } = new();
    public FakeStayOperationRepository StayOperations { get; } = new();
    public MutableClock Clock { get; } = new();
    public CountingIdGenerator Ids { get; } = new();
    public ReservationMutationCoordinator Mutations { get; }
    public ReservationStayAmendmentCoordinator Coordinator { get; }
    public AmendReservationStayCommandHandler AmendHandler => new(this.Coordinator);

    public sealed class FakeReservationRepository(Reservation reservation)
        : IReservationRepository
    {
        public Task AddAsync(Reservation value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) => Task.FromResult<Reservation?>(
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
            CancellationToken cancellationToken) => Task.FromResult<Reservation?>(
                reservation.Id == reservationId ? reservation : null);

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

    public sealed class FakeInventoryProjectionRepository : IInventoryProjectionRepository
    {
        public InventoryUnitSelectionValidation Selection { get; set; } =
            InventoryUnitSelectionValidation.Valid;
        public int ValidationCount { get; private set; }
        public int AllocationApplyCount { get; private set; }
        public ReservationInventoryAllocationWriteModel? LastAppliedAllocation { get; private set; }

        public Task<InventoryUnitSelectionValidation> ValidateSelectionAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken)
        {
            this.ValidationCount++;
            return Task.FromResult(this.Selection);
        }

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

        public Task ApplyAllocationAsync(
            ReservationInventoryAllocationWriteModel allocation,
            CancellationToken cancellationToken)
        {
            this.AllocationApplyCount++;
            this.LastAppliedAllocation = allocation;
            return Task.CompletedTask;
        }

        public Task ReleaseAllocationAsync(
            string scopeId,
            Guid allocationId,
            Guid reservationId,
            long version,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public sealed class FakeManagementOperationRepository
        : IReservationManagementOperationRepository
    {
        public List<ReservationManagementOperationRecord> Items { get; } = [];

        public Task<ReservationManagementOperationRecord?> GetAsync(
            Guid reservationId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
                this.Items.SingleOrDefault(item =>
                    item.ReservationId == reservationId && item.OperationId == operationId));

        public Task AddAsync(
            ReservationManagementOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Items.Add(operation);
            return Task.CompletedTask;
        }
    }

    public sealed class FakeStayOperationRepository
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
            CancellationToken cancellationToken) => Task.FromResult(new ReservationStayAmendmentRecoveryPageRecord(
                this.Items.Where(item =>
                    item.PropertyId == propertyId &&
                    item.Outcome is ReservationStayAmendmentOperationOutcome.Pending or
                        ReservationStayAmendmentOperationOutcome.OutcomeUnknown)
                    .Take(pageSize)
                    .ToArray(),
                NextCursor: null));
    }

    public sealed class MutableClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    public sealed class CountingIdGenerator : IIdGenerator
    {
        public int Count { get; private set; }

        public Guid NewId()
        {
            this.Count++;
            return Guid.NewGuid();
        }
    }

    private static Reservation CreateConfirmedReservation(
        IReadOnlyCollection<Guid> unitIds,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime)
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Arrival,
            Departure,
            unitIds,
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
            Now,
            expectedArrivalTime,
            expectedDepartureTime).Value;
        AssertSuccess(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now));
        return reservation;
    }

    private static void AssertSuccess(Gma.Framework.Results.Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Code);
        }
    }

    public static readonly DateOnly Arrival = new(2026, 8, 20);
    public static readonly DateOnly Departure = new(2026, 8, 23);
    public static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}
