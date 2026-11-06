namespace BunkFy.Modules.Reservations.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.Models;
using Gma.Framework.Messaging;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationProcessingRestrictionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overlapping_approved_restrictions_fail_closed_until_both_are_released()
    {
        Reservation reservation = CreateReservation();
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(reservation);
        RecordingRestrictionRepository restrictions = new();
        RecordingReminderRepository reminders = new();
        RecordingApprovalGate approval = new();
        RecordingCountryPolicy countryPolicy = new();
        QueueIdGenerator ids = new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());
        ApplyReservationProcessingRestrictionCommandHandler apply = new(
            ReservationMutationTestSupport.Create(
                new RecordingReservationRepository(reservation)),
            new RecordingProjectionRepository(projection),
            restrictions,
            reminders,
            approval,
            countryPolicy,
            new TestScopeContext(),
            new TestClock(),
            ids);

        ApplyReservationProcessingRestrictionCommand first =
            CreateApplyCommand(reservation, expectedProjectionRevision: 0);
        ApplyReservationProcessingRestrictionCommand second =
            CreateApplyCommand(reservation, expectedProjectionRevision: 1);
        Result<ReservationProcessingRestrictionReceiptDto> firstResult =
            await apply.HandleAsync(first, CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> secondResult =
            await apply.HandleAsync(second, CancellationToken.None);

        Assert.True(firstResult.IsSuccess, firstResult.Error.Code);
        Assert.True(secondResult.IsSuccess, secondResult.Error.Code);
        Assert.True(projection.IsRestricted);
        Assert.Equal(2, projection.ActiveRestrictionCount);
        Assert.Equal(2, projection.Revision);
        Assert.Equal(1, reminders.SuppressCount);
        Assert.Equal(2, restrictions.Restrictions.Count);
        Assert.All(
            approval.Requests,
            request =>
            {
                Assert.Equal(DataRightsOperation.Restriction, request.Operation);
                Assert.Equal(
                    DataRightsRestrictionDirective.Apply,
                    request.RestrictionDirective);
            });
        Assert.All(
            countryPolicy.PurposeCodes,
            purpose => Assert.Equal(
                ReservationCountryPolicyAdmission.DataRightsRestrictionPurpose,
                purpose));

        ReleaseReservationProcessingRestrictionCommandHandler release = new(
            ReservationMutationTestSupport.Create(
                new RecordingReservationRepository(reservation)),
            new RecordingProjectionRepository(projection),
            restrictions,
            approval,
            countryPolicy,
            new TestScopeContext(),
            new TestClock(),
            ids);
        ReservationProcessingRestriction firstRestriction =
            restrictions.Restrictions[0];
        ReservationProcessingRestriction secondRestriction =
            restrictions.Restrictions[1];
        ReleaseReservationProcessingRestrictionCommand firstReleaseCommand =
            CreateReleaseCommand(
                reservation,
                firstRestriction,
                expectedProjectionRevision: 2);
        ReleaseReservationProcessingRestrictionCommand secondReleaseCommand =
            CreateReleaseCommand(
                reservation,
                secondRestriction,
                expectedProjectionRevision: 3) with
            {
                LegacyUnboundTarget = true
            };
        Result<ReservationProcessingRestrictionReceiptDto> firstRelease =
            await release.HandleAsync(
                firstReleaseCommand,
                CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> secondRelease =
            await release.HandleAsync(
                secondReleaseCommand,
                CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> changedActorReplay =
            await release.HandleAsync(
                firstReleaseCommand with { ActorId = "user:other-operator" },
                CancellationToken.None);

        Assert.True(firstRelease.IsSuccess, firstRelease.Error.Code);
        Assert.True(firstRelease.Value.EffectiveRestricted);
        Assert.True(secondRelease.IsSuccess, secondRelease.Error.Code);
        Assert.False(secondRelease.Value.EffectiveRestricted);
        Assert.Equal(
            ReservationsApplicationErrors
                .ProcessingRestrictionIdempotencyConflict,
            changedActorReplay.Error);
        Assert.False(projection.IsRestricted);
        Assert.Equal(0, projection.ActiveRestrictionCount);
        Assert.Equal(4, projection.Revision);
        Assert.Equal(4, restrictions.Receipts.Count);
        Assert.Equal(
            [
                ReservationProcessingRestrictionAction.Apply,
                ReservationProcessingRestrictionAction.Apply,
                ReservationProcessingRestrictionAction.Release,
                ReservationProcessingRestrictionAction.Release
            ],
            restrictions.Receipts.Select(receipt => receipt.Action));
        Assert.Collection(
            approval.Requests.Skip(2),
            request =>
            {
                Assert.Equal(
                    firstReleaseCommand.RestrictionId,
                    request.RestrictionTargetOwnerOperationId);
                Assert.Equal(
                    firstReleaseCommand.ExpectedRestrictionVersion,
                    request.RestrictionTargetOwnerOperationVersion);
            },
            request =>
            {
                Assert.Null(request.RestrictionTargetOwnerOperationId);
                Assert.Null(request.RestrictionTargetOwnerOperationVersion);
            });
        Assert.DoesNotContain(
            typeof(ReservationProcessingRestrictionReceipt).GetProperties(),
            property => property.Name is
                "ActorId" or "AppliedBy" or "ReleasedBy" or "PrimaryGuestName" or
                "Email" or "Phone" or "Notes");
    }

    [Fact]
    public async Task Exact_replay_bypasses_approval_and_mismatched_replay_is_rejected()
    {
        Reservation reservation = CreateReservation();
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(reservation);
        RecordingRestrictionRepository restrictions = new();
        RecordingReminderRepository reminders = new();
        RecordingApprovalGate approval = new();
        ApplyReservationProcessingRestrictionCommand command =
            CreateApplyCommand(reservation, expectedProjectionRevision: 0);
        ApplyReservationProcessingRestrictionCommandHandler handler = new(
            ReservationMutationTestSupport.Create(
                new RecordingReservationRepository(reservation)),
            new RecordingProjectionRepository(projection),
            restrictions,
            reminders,
            approval,
            new RecordingCountryPolicy(),
            new TestScopeContext(),
            new TestClock(),
            new QueueIdGenerator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Result<ReservationProcessingRestrictionReceiptDto> applied =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> changedActorReplay =
            await handler.HandleAsync(
                command with { ActorId = "user:other-operator" },
                CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> conflict =
            await handler.HandleAsync(
                command with { CaseId = Guid.NewGuid() },
                CancellationToken.None);
        Result<ReservationProcessingRestrictionReceiptDto> reusedApproval =
            await handler.HandleAsync(
                command with
                {
                    IdempotencyKey = Guid.NewGuid(),
                    ExpectedProjectionRevision = 1
                },
                CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(applied.Value.ReceiptId, replay.Value.ReceiptId);
        Assert.Equal(
            ReservationsApplicationErrors
                .ProcessingRestrictionIdempotencyConflict,
            changedActorReplay.Error);
        Assert.Equal(
            ReservationsApplicationErrors.ProcessingRestrictionIdempotencyConflict,
            conflict.Error);
        Assert.Equal(
            ReservationsApplicationErrors.ProcessingRestrictionApprovalAlreadyUsed,
            reusedApproval.Error);
        Assert.Equal(2, approval.Requests.Count);
        Assert.Equal(1, reminders.SuppressCount);
        Assert.Single(restrictions.Restrictions);
        Assert.Single(restrictions.Receipts);
    }

    [Fact]
    public async Task Approval_denial_leaves_projection_and_evidence_unchanged()
    {
        Reservation reservation = CreateReservation();
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(reservation);
        RecordingRestrictionRepository restrictions = new();
        RecordingReminderRepository reminders = new();
        ApplyReservationProcessingRestrictionCommandHandler handler = new(
            ReservationMutationTestSupport.Create(
                new RecordingReservationRepository(reservation)),
            new RecordingProjectionRepository(projection),
            restrictions,
            reminders,
            new RecordingApprovalGate(isApproved: false),
            new RecordingCountryPolicy(),
            new TestScopeContext(),
            new TestClock(),
            new QueueIdGenerator());

        Result<ReservationProcessingRestrictionReceiptDto> result =
            await handler.HandleAsync(
                CreateApplyCommand(reservation, expectedProjectionRevision: 0),
                CancellationToken.None);

        Assert.Equal(ReservationsApplicationErrors.DataRightsApprovalRequired, result.Error);
        Assert.False(projection.IsRestricted);
        Assert.Equal(0, projection.Revision);
        Assert.Empty(restrictions.Restrictions);
        Assert.Empty(restrictions.Receipts);
        Assert.Equal(0, reminders.SuppressCount);
    }

    [Fact]
    public async Task Receipt_event_projects_one_pii_free_outbox_event()
    {
        Reservation reservation = CreateReservation();
        ReservationProcessingRestrictionReceipt receipt =
            ReservationProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                reservation.ScopeId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                ReservationProcessingRestrictionAction.Apply,
                reservation.PropertyId,
                reservation.Id,
                Guid.NewGuid(),
                approvalRevision: 2,
                reservation.Version,
                ReservationProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                Guid.NewGuid(),
                Now).Value;
        ReservationProcessingRestrictionChangedDomainEvent domainEvent =
            Assert.IsType<ReservationProcessingRestrictionChangedDomainEvent>(
                Assert.Single(receipt.DomainEvents));
        RecordingOutbox outbox = new();

        await new ReservationProcessingRestrictionOutboxProjector(
                new RecordingOutboxRegistry(outbox))
            .HandleAsync(domainEvent, CancellationToken.None);

        ReservationProcessingRestrictionChangedIntegrationEvent integrationEvent =
            Assert.IsType<ReservationProcessingRestrictionChangedIntegrationEvent>(
                Assert.Single(outbox.Events));
        Assert.Equal(domainEvent.EventId, integrationEvent.EventId);
        Assert.Equal(domainEvent.ScopeId, integrationEvent.ScopeId);
        Assert.Equal(domainEvent.ReservationId, integrationEvent.ReservationId);
        Assert.True(integrationEvent.IsRestricted);
        Assert.DoesNotContain(
            integrationEvent.GetType().GetProperties(),
            property => property.Name is
                "ActorId" or "AppliedBy" or "ReleasedBy" or "PrimaryGuestName" or
                "Email" or "Phone" or "Notes" or "CaseId");
    }

    private static ApplyReservationProcessingRestrictionCommand CreateApplyCommand(
        Reservation reservation,
        long expectedProjectionRevision) =>
        new(
            Guid.NewGuid(),
            reservation.PropertyId,
            Guid.NewGuid(),
            ApprovalRevision: 2,
            reservation.Id,
            reservation.Version,
            expectedProjectionRevision,
            "user:privacy-operator");

    private static ReleaseReservationProcessingRestrictionCommand CreateReleaseCommand(
        Reservation reservation,
        ReservationProcessingRestriction restriction,
        long expectedProjectionRevision) =>
        new(
            Guid.NewGuid(),
            reservation.PropertyId,
            restriction.Id,
            Guid.NewGuid(),
            ApprovalRevision: 3,
            reservation.Id,
            reservation.Version,
            restriction.Version,
            expectedProjectionRevision,
            "user:privacy-operator");

    private static ReservationProcessingRestrictionProjection CreateProjection(
        Reservation reservation) =>
        ReservationProcessingRestrictionProjection.Create(
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.Id,
            ReservationProcessingRestrictionContract.CurrentVersion,
            Now.AddMinutes(-1)).Value;

    private static Reservation CreateReservation() => Reservation.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        Guid.NewGuid(),
        new DateOnly(2026, 8, 1),
        new DateOnly(2026, 8, 3),
        [Guid.NewGuid()],
        "Original Guest",
        "original@example.test",
        "+44 20 1234 5678",
        guestCount: 1,
        ReservationSource.Direct,
        sourceSystem: null,
        sourceReference: null,
        notes: "Late arrival",
        eventId: Guid.NewGuid(),
        detailsEventId: Guid.NewGuid(),
        initialDetailsOrigin: ReservationDetailsChangeOrigin.Staff,
        initialDetailsActorId: "user:creator",
        initialAdapterConnectionId: null,
        initialExternalOperationId: null,
        initialCorrelationId: Guid.NewGuid(),
        nowUtc: Now.AddDays(-1),
        expectedArrivalTime: new TimeOnly(15, 0),
        expectedDepartureTime: new TimeOnly(11, 0)).Value;

    private sealed class RecordingReservationRepository(Reservation reservation)
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
            Task.FromResult<Reservation?>(null);

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

    private sealed class RecordingProjectionRepository(
        ReservationProcessingRestrictionProjection projection)
        : IReservationProcessingRestrictionProjectionRepository
    {
        public Task<ReservationProcessingRestrictionProjection?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReservationProcessingRestrictionProjection?>(
                projection.PropertyId == propertyId &&
                projection.ReservationId == reservationId
                    ? projection
                    : null);

        public Task EnsureAsync(
            string tenantId,
            Guid propertyId,
            Guid reservationId,
            DateTimeOffset initializedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRestrictionRepository
        : IReservationProcessingRestrictionRepository
    {
        public List<ReservationProcessingRestriction> Restrictions { get; } = [];
        public List<ReservationProcessingRestrictionReceipt> Receipts { get; } = [];

        public Task<ReservationProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Receipts.SingleOrDefault(
                receipt => receipt.IdempotencyKey == idempotencyKey));

        public Task<ReservationProcessingRestriction?> FindByApplyApprovalAsync(
            Guid propertyId,
            Guid reservationId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Restrictions.SingleOrDefault(
                restriction =>
                    restriction.PropertyId == propertyId &&
                    restriction.ReservationId == reservationId &&
                    restriction.ApplyCaseId == caseId &&
                    restriction.ApplyApprovalRevision == approvalRevision));

        public Task<ReservationProcessingRestriction?> FindByReleaseApprovalAsync(
            Guid propertyId,
            Guid reservationId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Restrictions.SingleOrDefault(
                restriction =>
                    restriction.PropertyId == propertyId &&
                    restriction.ReservationId == reservationId &&
                    restriction.ReleaseCaseId == caseId &&
                    restriction.ReleaseApprovalRevision == approvalRevision));

        public Task<ReservationProcessingRestriction?> GetAsync(
            Guid propertyId,
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Restrictions.SingleOrDefault(
                restriction =>
                    restriction.PropertyId == propertyId &&
                    restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<ReservationProcessingRestriction>>
            ListActiveAsync(
                Guid propertyId,
                Guid reservationId,
                PageRequest pageRequest,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ReservationProcessingRestriction>>(
                this.Restrictions
                    .Where(restriction =>
                        restriction.PropertyId == propertyId &&
                        restriction.ReservationId == reservationId &&
                        restriction.Status ==
                            ReservationProcessingRestrictionStatus.Active)
                    .OrderBy(restriction => restriction.AppliedAtUtc)
                    .ThenBy(restriction => restriction.Id)
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());

        public Task AddAsync(
            ReservationProcessingRestriction restriction,
            CancellationToken cancellationToken)
        {
            this.Restrictions.Add(restriction);
            return Task.CompletedTask;
        }

        public Task AddReceiptAsync(
            ReservationProcessingRestrictionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Receipts.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReminderRepository
        : IReservationArrivalReminderRepository
    {
        public int SuppressCount { get; private set; }

        public Task ApplyPropertyAsync(
            ReservationReminderPropertyWriteModel property,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RefreshReservationAsync(
            ReservationReminderSource reservation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SuppressForProcessingRestrictionAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            this.SuppressCount++;
            return Task.CompletedTask;
        }

        public Task<ReservationArrivalReminderClaimResult> ClaimDueAsync(
            DateTimeOffset nowUtc,
            int batchSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListScheduleScopeIdsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingApprovalGate(bool isApproved = true)
        : IDataRightsOperationApprovalGate
    {
        public List<DataRightsOperationApprovalRequest> Requests { get; } = [];

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(isApproved
                ? DataRightsOperationApprovalResult.Approved
                : DataRightsOperationApprovalResult.Denied(
                    DataRightsOperationApprovalDenial.CaseNotApproved));
        }
    }

    private sealed class RecordingCountryPolicy : IReservationCountryPolicyAdmission
    {
        public List<string> PurposeCodes { get; } = [];

        public Task<CountryPolicyDecision> EvaluateAsync(
            Guid propertyId,
            string purposeCode,
            CountryPolicySurface surface,
            string sourceProvenance,
            CancellationToken cancellationToken)
        {
            this.PurposeCodes.Add(purposeCode);
            return Task.FromResult(CountryPolicyDecision.Allow(
                new CountryPolicyEvidence(
                    "GB",
                    "gb-hostel",
                    1,
                    "eu-west-2",
                    "uk-no-transfer",
                    "reservation-operational",
                    1,
                    new string('a', 64),
                    purposeCode,
                    surface,
                    sourceProvenance,
                    CountryPolicyApprovalState.Approved,
                    Now.AddDays(-1),
                    Now.AddDays(30),
                    Now,
                    [])));
        }
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => ReservationsModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class QueueIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> values = new(ids);

        public Guid NewId() => this.values.Dequeue();
    }
}
