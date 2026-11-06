namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Retention;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyReservationRetentionCommandHandlerTests
{
    [Fact]
    public async Task Due_reservation_commits_record_and_both_proofs_once()
    {
        Fixture fixture = CreateFixture("tenant-a");
        ApplyReservationRetentionCommand command = new(
            fixture.Execution.Id,
            Attempt: 1,
            fixture.Reservation.PropertyId,
            fixture.Reservation.Id,
            fixture.Reservation.Version,
            fixture.Reservation.DetailsRevision);

        Result<ReservationRetentionMutationResult> first =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);
        Result<ReservationRetentionMutationResult> replay =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            ReservationRetentionMutationStatus.Applied,
            first.Value.Status);
        Assert.Equal(
            ReservationRetentionMutationStatus.AlreadyApplied,
            replay.Value.Status);
        Assert.True(fixture.Reservation.IsAnonymised);
        Assert.Equal(1, fixture.Execution.AffectedCount);
        Assert.Equal(1, fixture.Executions.AddProofCount);
        Assert.Equal(1, fixture.Candidates.LoadCount);
        Assert.Equal(1, fixture.OperationLock.CallCount);
        Assert.NotNull(fixture.Executions.Receipt);
        Assert.NotNull(fixture.Executions.Tombstone);
        Assert.True(fixture.Executions.Tombstone.MatchesRetention(
            fixture.Executions.Receipt));
    }

    [Fact]
    public async Task Cross_tenant_execution_is_rejected_before_discovery()
    {
        Fixture fixture = CreateFixture("tenant-b");

        Result<ReservationRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Reservation.PropertyId,
                    fixture.Reservation.Id,
                    fixture.Reservation.Version,
                    fixture.Reservation.DetailsRevision),
                CancellationToken.None);

        Assert.Equal(
            ReservationsApplicationErrors.RetentionExecutionNotFound,
            result.Error);
        Assert.Equal(0, fixture.Candidates.LoadCount);
        Assert.Equal(0, fixture.OperationLock.CallCount);
        Assert.False(fixture.Reservation.IsAnonymised);
    }

    [Fact]
    public async Task Stale_attempt_is_rejected_before_discovery()
    {
        Fixture fixture = CreateFixture("tenant-a");

        Result<ReservationRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 2,
                    fixture.Reservation.PropertyId,
                    fixture.Reservation.Id,
                    fixture.Reservation.Version,
                    fixture.Reservation.DetailsRevision),
                CancellationToken.None);

        Assert.Equal(
            ReservationsApplicationErrors.RetentionExecutionNotFound,
            result.Error);
        Assert.Equal(0, fixture.Candidates.LoadCount);
        Assert.Equal(0, fixture.OperationLock.CallCount);
        Assert.False(fixture.Reservation.IsAnonymised);
    }

    [Fact]
    public async Task Active_hold_observed_under_lock_blocks_mutation()
    {
        Fixture fixture = CreateFixture("tenant-a");
        fixture.Candidates.Snapshot =
            fixture.Candidates.Snapshot with
            {
                ActiveHoldCount = 1,
                EarliestHoldPlacedAtUtc =
                    ReservationRetentionTestData.Now.AddDays(-2)
            };

        Result<ReservationRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Reservation.PropertyId,
                    fixture.Reservation.Id,
                    fixture.Reservation.Version,
                    fixture.Reservation.DetailsRevision),
                CancellationToken.None);

        Assert.Equal(
            ReservationRetentionMutationStatus.Blocked,
            result.Value.Status);
        Assert.False(fixture.Reservation.IsAnonymised);
        Assert.Equal(0, fixture.Execution.AffectedCount);
        Assert.Equal(0, fixture.Executions.AddProofCount);
        Assert.Equal(1, fixture.OperationLock.CallCount);
    }

    private static Fixture CreateFixture(string activeScopeId)
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        Reservation reservation = CreateTerminalReservation();
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
                Guid.NewGuid(),
                reservation.ScopeId,
                ReservationRetentionCoordinates.DataClassKey,
                ReservationRetentionCoordinates
                    .ExecutionPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                ReservationRetentionTestData.Now.AddMinutes(-1),
                ReservationRetentionTestData.Now.AddMinutes(10)).Value;
        RecordingExecutionRepository executions =
            new(execution);
        RecordingCandidateRepository candidates = new(
            ReservationRetentionTestData.Snapshot(
                policy.Binding,
                terminalAtUtc: reservation.TerminalAtUtc,
                reservationVersion: reservation.Version,
                detailsRevision: reservation.DetailsRevision,
                reservationId: reservation.Id,
                propertyId: reservation.PropertyId));
        RecordingOperationLock operationLock = new();
        StubReservationRepository reservations = new(reservation);
        TestScopeContext scopeContext = new(activeScopeId);
        ApplyReservationRetentionCommandHandler handler = new(
            executions,
            candidates,
            reservations,
            new StubAnonymisationRepository(),
            ReservationMutationTestSupport.Create(
                reservations,
                operationLock,
                scopeContext),
            new ReservationRetentionEligibilityEvaluator(
                policy.Registry),
            scopeContext,
            new TestClock(),
            new QueueIdGenerator(
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new(
            handler,
            executions,
            candidates,
            operationLock,
            execution,
            reservation);
    }

    private static Reservation CreateTerminalReservation()
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2025, 6, 1),
            new DateOnly(2025, 6, 3),
            [Guid.NewGuid()],
            "Original Guest",
            "original@example.test",
            "+44 20 1234 5678",
            1,
            ReservationSource.Direct,
            null,
            null,
            "Late arrival",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            "user:creator",
            null,
            null,
            Guid.NewGuid(),
            ReservationRetentionTestData.Now.AddDays(-401),
            new TimeOnly(15, 0),
            new TimeOnly(11, 0)).Value;
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.UnitNotSellable,
            Guid.NewGuid(),
            ReservationRetentionTestData.Now.AddDays(-400))
            .IsSuccess);
        reservation.ClearDomainEvents();
        return reservation;
    }

    private sealed record Fixture(
        ApplyReservationRetentionCommandHandler Handler,
        RecordingExecutionRepository Executions,
        RecordingCandidateRepository Candidates,
        RecordingOperationLock OperationLock,
        ReservationRetentionExecution Execution,
        Reservation Reservation);

    private sealed class RecordingExecutionRepository(
        ReservationRetentionExecution execution)
        : IReservationRetentionExecutionRepository
    {
        public ReservationRetentionAnonymisationReceipt? Receipt
        {
            get;
            private set;
        }

        public ReservationAnonymisationTombstone? Tombstone
        {
            get;
            private set;
        }

        public int AddProofCount { get; private set; }

        public Task<ReservationRetentionExecution?>
            GetExecutionAsync(
                Guid executionId,
                CancellationToken cancellationToken) =>
            Task.FromResult<ReservationRetentionExecution?>(
                execution.Id == executionId
                    ? execution
                    : null);

        public Task AddExecutionAsync(
            ReservationRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationRetentionSweepCheckpoint?>
            GetCheckpointAsync(
                string dataClassKey,
                int executionPolicyVersion,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCheckpointAsync(
            ReservationRetentionSweepCheckpoint checkpoint,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationRetentionAnonymisationReceipt?>
            GetReceiptAsync(
                Guid reservationId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Receipt?.ReservationId == reservationId
                    ? this.Receipt
                    : null);

        public Task<ReservationAnonymisationTombstone?>
            GetTombstoneAsync(
                Guid reservationId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Tombstone?.Id == reservationId
                    ? this.Tombstone
                    : null);

        public Task AddAnonymisationProofAsync(
            ReservationRetentionAnonymisationReceipt receipt,
            ReservationAnonymisationTombstone tombstone,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            this.Tombstone = tombstone;
            this.AddProofCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCandidateRepository(
        ReservationRetentionCandidateSnapshot snapshot)
        : IReservationRetentionCandidateRepository
    {
        public ReservationRetentionCandidateSnapshot Snapshot
        {
            get;
            set;
        } = snapshot;

        public int LoadCount { get; private set; }

        public Task<ReservationRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationRetentionCandidateSnapshot?> LoadAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            this.LoadCount++;
            return Task.FromResult<
                ReservationRetentionCandidateSnapshot?>(
                this.Snapshot.PropertyId == propertyId &&
                this.Snapshot.ReservationId == reservationId
                    ? this.Snapshot
                    : null);
        }
    }

    private sealed class StubReservationRepository(
        Reservation reservation)
        : IReservationRepository
    {
        public Task AddAsync(
            Reservation value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetForDataRightsAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Reservation?>(
                reservation.PropertyId == propertyId &&
                reservation.Id == reservationId
                    ? reservation
                    : null);

        public Task<Reservation?> GetAsyncByReservationId(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

    private sealed class StubAnonymisationRepository
        : IReservationAnonymisationRepository
    {
        public Task<ReservationAnonymisationReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationAnonymisationAffectedRecords>
            RedactOwnedRecordsAsync(
                Reservation reservation,
                ReservationAnonymisationOutcome outcome,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new ReservationAnonymisationAffectedRecords(
                    RedactedHistoryCount: 2,
                    ReducedExternalOperationCount: 1,
                    SuppressedReminderCount: 1));

        public Task<bool> VerifyOwnerStateAsync(
            ReservationAnonymisationReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddOwnerProofAsync(
            ReservationAnonymisationReceipt receipt,
            ReservationAnonymisationTombstone tombstone,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOperationLock
        : IReservationOperationLock
    {
        public int CallCount { get; private set; }

        public Task<bool> TryAcquireExistingAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            this.CallCount++;
            return Task.FromResult(true);
        }

        public Task AcquireCoordinateAsync(
            string tenantId,
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            this.CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow =>
            ReservationRetentionTestData.Now;
    }

    private sealed class QueueIdGenerator(params Guid[] values)
        : IIdGenerator
    {
        private readonly Queue<Guid> values = new(values);

        public Guid NewId() => this.values.Dequeue();
    }
}
