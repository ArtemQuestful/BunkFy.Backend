namespace BunkFy.Modules.Reservations.Tests;

using System.Reflection;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Pagination;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationMutationCoordinatorTests
{
    [Fact]
    public async Task Creation_acquires_coordinate_before_authoritative_read()
    {
        Reservation reservation = CreateReservation();
        List<string> calls = [];
        SequencedReservationRepository reservations = new(reservation, calls);
        ReservationMutationCoordinator coordinator =
            ReservationMutationTestSupport.Create(
                reservations,
                new CallbackOperationLock(calls));

        Reservation? result = await coordinator.AcquireCreationAsync(
            reservation.Id,
            CancellationToken.None);

        Assert.Same(reservation, result);
        Assert.Equal(["coordinate-lock", "creation-replay-read"], calls);
    }

    [Fact]
    public async Task Operational_acquire_locks_before_authoritative_reload()
    {
        Reservation reservation = CreateReservation();
        List<string> calls = [];
        SequencedReservationRepository reservations = new(reservation, calls);
        CallbackOperationLock operationLock = new(
            calls,
            () => reservations.OperationalVisible = false);
        ReservationMutationCoordinator coordinator =
            ReservationMutationTestSupport.Create(
                reservations,
                operationLock);

        Reservation? result = await coordinator.AcquireOperationalAsync(
            reservation.PropertyId,
            reservation.Id,
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["lock", "operational-read"], calls);
    }

    [Fact]
    public async Task Required_continuation_serializes_but_bypasses_operational_visibility()
    {
        Reservation reservation = CreateReservation();
        List<string> calls = [];
        SequencedReservationRepository reservations = new(reservation, calls)
        {
            OperationalVisible = false
        };
        ReservationMutationCoordinator coordinator =
            ReservationMutationTestSupport.Create(
                reservations,
                new CallbackOperationLock(calls));

        Reservation? result =
            await coordinator.AcquireRequiredContinuationAsync(
                reservation.PropertyId,
                reservation.Id,
                CancellationToken.None);

        Assert.Same(reservation, result);
        Assert.Equal(["lock", "continuation-read"], calls);
    }

    [Theory]
    [InlineData(typeof(CancelReservationCommandHandler))]
    [InlineData(typeof(UpdateReservationGuestDetailsCommandHandler))]
    [InlineData(typeof(LinkReservationGuestCommandHandler))]
    [InlineData(typeof(ReassignReservationInventoryCommandHandler))]
    [InlineData(typeof(CheckInReservationCommandHandler))]
    [InlineData(typeof(MarkReservationNoShowCommandHandler))]
    [InlineData(typeof(CheckOutReservationCommandHandler))]
    [InlineData(typeof(ApplyReservationDataRightsCorrectionCommandHandler))]
    [InlineData(typeof(ApplyReservationProcessingRestrictionCommandHandler))]
    [InlineData(typeof(ReleaseReservationProcessingRestrictionCommandHandler))]
    [InlineData(typeof(PlaceReservationDataHoldCommandHandler))]
    [InlineData(typeof(ReleaseReservationDataHoldCommandHandler))]
    [InlineData(typeof(ApplyReservationAnonymisationCommandHandler))]
    [InlineData(typeof(ApplyReservationRetentionCommandHandler))]
    [InlineData(typeof(ExternalReservationGuestDetailsChangeRequestedHandler))]
    [InlineData(typeof(ExternalReservationAmendmentRequestedHandler))]
    [InlineData(typeof(ExternalReservationCancellationRequestedHandler))]
    [InlineData(typeof(InventoryAllocationConfirmedHandler))]
    [InlineData(typeof(InventoryAllocationRejectedHandler))]
    [InlineData(typeof(InventoryAllocationReleasedHandler))]
    [InlineData(typeof(InventoryAllocationReleaseRejectedHandler))]
    [InlineData(typeof(InventoryAllocationAmendmentConfirmedHandler))]
    [InlineData(typeof(InventoryAllocationAmendmentRejectedHandler))]
    public void Existing_reservation_writers_require_mutation_coordinator(
        Type handlerType)
    {
        bool hasCoordinator = HasConstructorDependency(
            handlerType,
            typeof(ReservationMutationCoordinator)) ||
            HasConstructorDependency(
                handlerType,
                typeof(ReservationManagementLifecycleCoordinator)) ||
            HasConstructorDependency(
                handlerType,
                typeof(ReservationStayAmendmentCoordinator));

        Assert.True(
            hasCoordinator,
            $"{handlerType.Name} must serialize through " +
            $"{nameof(ReservationMutationCoordinator)}.");
    }

    [Fact]
    public void Management_lifecycle_coordinator_owns_the_mutation_boundary()
    {
        Assert.True(HasConstructorDependency(
            typeof(ReservationManagementLifecycleCoordinator),
            typeof(ReservationMutationCoordinator)));
    }

    [Fact]
    public void Stay_amendment_coordinator_owns_the_mutation_boundary()
    {
        Assert.True(HasConstructorDependency(
            typeof(ReservationStayAmendmentCoordinator),
            typeof(ReservationMutationCoordinator)));
    }

    [Fact]
    public void Restore_keeps_an_explicit_coordinate_lock()
    {
        bool hasOperationLock = typeof(
                RestoreReservationAnonymisationCommandHandler)
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter =>
                parameter.ParameterType == typeof(IReservationOperationLock));

        Assert.True(hasOperationLock);
    }

    [Fact]
    public void Management_creation_requires_mutation_coordinator()
    {
        bool hasCoordinator = typeof(CreateReservationCommandHandler)
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter =>
                parameter.ParameterType == typeof(ReservationMutationCoordinator));

        Assert.True(hasCoordinator);
    }

    private static bool HasConstructorDependency(
        Type ownerType,
        Type dependencyType) => ownerType
        .GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic)
        .SelectMany(constructor => constructor.GetParameters())
        .Any(parameter => parameter.ParameterType == dependencyType);

    private static Reservation CreateReservation() => Reservation.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        Guid.NewGuid(),
        new DateOnly(2026, 8, 10),
        new DateOnly(2026, 8, 12),
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
        initialDetailsActorId: "user:creator",
        initialAdapterConnectionId: null,
        initialExternalOperationId: null,
        Guid.NewGuid(),
        new DateTimeOffset(2026, 8, 6, 6, 0, 0, TimeSpan.Zero)).Value;

    private sealed class CallbackOperationLock(
        List<string> calls,
        Action? acquired = null)
        : IReservationOperationLock
    {
        public Task<bool> TryAcquireExistingAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            calls.Add("lock");
            acquired?.Invoke();
            return Task.FromResult(true);
        }

        public Task AcquireCoordinateAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            calls.Add("coordinate-lock");
            acquired?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedReservationRepository(
        Reservation reservation,
        List<string> calls)
        : IReservationRepository
    {
        public bool OperationalVisible { get; set; } = true;

        public Task AddAsync(
            Reservation value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            calls.Add("operational-read");
            return Task.FromResult<Reservation?>(
                this.OperationalVisible &&
                reservation.PropertyId == propertyId &&
                reservation.Id == reservationId
                    ? reservation
                    : null);
        }

        public Task<Reservation?> GetForDataRightsAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(propertyId, reservationId, cancellationToken);

        public Task<Reservation?> GetAsyncByReservationId(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Reservation?>(
                reservation.Id == reservationId ? reservation : null);

        public Task<Reservation?> GetForRequiredContinuationAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            calls.Add("continuation-read");
            return Task.FromResult<Reservation?>(
                reservation.PropertyId == propertyId &&
                reservation.Id == reservationId
                    ? reservation
                    : null);
        }

        public Task<Reservation?>
            GetForRequiredContinuationByReservationIdAsync(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Creation replay must use its narrow repository path.");

        public Task<Reservation?> GetForCreationReplayByReservationIdAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            calls.Add("creation-replay-read");
            return this.GetAsyncByReservationId(
                reservationId,
                cancellationToken);
        }

        public Task<Reservation?> GetByExternalSourceAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExternalSourceExistsAsync(
            string sourceSystem,
            string sourceReference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationListResponse> ListAsync(
            Guid propertyId,
            IReadOnlyCollection<ReservationStatus>? statuses,
            string? search,
            ReservationListOrder order,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
