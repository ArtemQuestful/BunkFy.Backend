namespace BunkFy.Modules.Reservations.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationDataRightsRestrictionContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Application_registers_the_reservations_restriction_owner()
    {
        ServiceCollection services = new();

        services.AddReservationsApplication();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType ==
                typeof(IDataRightsRestrictionContributor));
        Assert.Equal(
            typeof(ReservationDataRightsRestrictionContributor),
            descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public async Task Committed_owner_receipt_replays_before_current_state_lookup()
    {
        DataRightsRestrictionContributionRequest request = CreateApplyRequest();
        ReservationProcessingRestriction restriction =
            CreateActiveRestriction(request, applyCaseId: request.CaseId);
        ReservationProcessingRestrictionReceipt receipt = CreateReceipt(
            request,
            ReservationProcessingRestrictionAction.Apply,
            effectiveRestricted: true,
            restrictionId: restriction.Id);
        RecordingRestrictionRepository repository = new(
            active: [restriction],
            receipt: receipt);
        RecordingDispatcher dispatcher = new(Result.Failure<
            ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors
                    .ProcessingRestrictionProjectionUnavailable));
        ReservationDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new NullProjectionRepository(),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        DataRightsRestrictionOwnerProof proof =
            Assert.IsType<DataRightsRestrictionOwnerProof>(result.OwnerProof);
        Assert.Equal(receipt.Id, proof.ReceiptId);
        Assert.Equal(
            DataRightsRestrictionContract.Sha256Length,
            proof.ReceiptSha256.Length);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(0, repository.ActiveListCount);
    }

    [Fact]
    public async Task Changed_actor_cannot_replay_a_committed_owner_receipt()
    {
        DataRightsRestrictionContributionRequest original = CreateApplyRequest();
        ReservationProcessingRestriction restriction =
            CreateActiveRestriction(original, applyCaseId: original.CaseId);
        ReservationProcessingRestrictionReceipt receipt = CreateReceipt(
            original,
            ReservationProcessingRestrictionAction.Apply,
            effectiveRestricted: true,
            restrictionId: restriction.Id);
        ReservationDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                ReservationProcessingRestrictionReceiptDto>(
                    ReservationsApplicationErrors
                        .ProcessingRestrictionProjectionUnavailable)),
            new NullProjectionRepository(),
            new RecordingRestrictionRepository(
                active: [restriction],
                receipt: receipt),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                original with { ExecutingActorId = "user:other-executor" },
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Failed,
            result.Status);
        Assert.Equal(
            ReservationsApplicationErrors
                .ProcessingRestrictionOwnerProofInvalid.Code,
            result.OutcomeCode);
    }

    [Fact]
    public async Task Apply_dispatches_the_exact_reservation_coordinate()
    {
        DataRightsRestrictionContributionRequest request = CreateApplyRequest() with
        {
            ExecutingActorId = " user:executor "
        };
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(request);
        ReservationProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            ReservationProcessingRestrictionActionDto.Apply,
            restrictionVersion: 1,
            projectionRevision: 1,
            effectiveRestricted: true);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        ReservationDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        ApplyReservationProcessingRestrictionCommand command =
            Assert.IsType<ApplyReservationProcessingRestrictionCommand>(
                dispatcher.Command);
        Assert.Equal(request.PropertyId, command.PropertyId);
        Assert.Equal(request.CaseId, command.CaseId);
        Assert.Equal(request.Coordinate.RecordId, command.ReservationId);
        Assert.Equal(
            request.Coordinate.RecordVersion,
            command.ExpectedReservationVersion);
        Assert.Equal(projection.Revision, command.ExpectedProjectionRevision);
        Assert.Equal("user:executor", command.ActorId);
    }

    [Fact]
    public async Task Targeted_release_dispatches_only_the_reviewed_active_restriction()
    {
        DataRightsRestrictionContributionRequest apply = CreateApplyRequest();
        ReservationProcessingRestriction[] active =
        [
            CreateActiveRestriction(apply),
            CreateActiveRestriction(apply)
        ];
        ReservationProcessingRestriction selected = active[1];
        DataRightsRestrictionContributionRequest request = CreateReleaseRequest(
            apply,
            selected);
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(request, activeRestrictionCount: 2);
        ReservationProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            ReservationProcessingRestrictionActionDto.Release,
            restrictionVersion: selected.Version + 1,
            projectionRevision: projection.Revision + 1,
            effectiveRestricted: true,
            restrictionId: selected.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        RecordingRestrictionRepository repository = new(active: active);
        ReservationDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        Assert.True(result.OwnerProof?.EffectiveRestricted);
        Assert.Equal(selected.Id, result.OwnerProof?.OwnerOperationId);
        Assert.Equal(0, repository.ActiveListCount);
        ReleaseReservationProcessingRestrictionCommand command =
            Assert.IsType<ReleaseReservationProcessingRestrictionCommand>(
                dispatcher.Command);
        Assert.Equal(selected.Id, command.RestrictionId);
        Assert.Equal(selected.Version, command.ExpectedRestrictionVersion);
        Assert.Equal(projection.Revision, command.ExpectedProjectionRevision);
        Assert.False(command.LegacyUnboundTarget);
    }

    [Fact]
    public async Task Legacy_unbound_release_requires_exactly_one_active_restriction()
    {
        DataRightsRestrictionContributionRequest apply = CreateApplyRequest();
        ReservationProcessingRestriction active = CreateActiveRestriction(apply);
        DataRightsRestrictionContributionRequest request =
            CreateLegacyReleaseRequest(apply);
        ReservationProcessingRestrictionProjection projection =
            CreateProjection(request, activeRestrictionCount: 1);
        ReservationProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            ReservationProcessingRestrictionActionDto.Release,
            restrictionVersion: active.Version + 1,
            projectionRevision: projection.Revision + 1,
            effectiveRestricted: false,
            restrictionId: active.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        RecordingRestrictionRepository repository = new(active: [active]);
        ReservationDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        Assert.False(result.OwnerProof?.EffectiveRestricted);
        Assert.Equal(1, repository.ActiveListCount);
        ReleaseReservationProcessingRestrictionCommand command =
            Assert.IsType<ReleaseReservationProcessingRestrictionCommand>(
                dispatcher.Command);
        Assert.True(command.LegacyUnboundTarget);
        Assert.Equal(active.Id, command.RestrictionId);
    }

    [Fact]
    public async Task Legacy_unbound_release_with_multiple_active_restrictions_fails_closed()
    {
        DataRightsRestrictionContributionRequest request =
            CreateLegacyReleaseRequest(CreateApplyRequest());
        ReservationProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        RecordingDispatcher dispatcher = new(Result.Failure<
            ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors
                    .ProcessingRestrictionProjectionUnavailable));
        ReservationDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(CreateProjection(request, 2)),
            new RecordingRestrictionRepository(active: active),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Blocked,
            result.Status);
        Assert.Equal(
            ReservationsApplicationErrors
                .ProcessingRestrictionActiveStateInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task Release_target_discovery_is_bounded_with_one_row_lookahead()
    {
        DataRightsRestrictionContributionRequest request = CreateApplyRequest();
        ReservationProcessingRestriction[] active = Enumerable.Range(
                0,
                DataRightsRestrictionContract.MaxReleaseTargets + 1)
            .Select(_ => CreateActiveRestriction(request))
            .ToArray();
        RecordingRestrictionRepository repository = new(active: active);
        ReservationDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                ReservationProcessingRestrictionReceiptDto>(
                    ReservationsApplicationErrors
                        .ProcessingRestrictionProjectionUnavailable)),
            new NullProjectionRepository(),
            repository,
            new TestClock());

        DataRightsRestrictionTargetResolutionResult result =
            await contributor.ResolveReleaseTargetsAsync(
                CreateTargetRequest(request),
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionTargetResolutionStatus.Completed,
            result.Status);
        Assert.Equal(
            DataRightsRestrictionContract.MaxReleaseTargets,
            result.Targets?.Count);
        Assert.True(result.LimitReached);
        Assert.Equal(
            DataRightsRestrictionContract.MaxReleaseTargets + 1,
            repository.LastPageRequest?.PageSize);
    }

    [Fact]
    public async Task Exact_target_resolution_distinguishes_missing_and_stale_state()
    {
        DataRightsRestrictionContributionRequest request = CreateApplyRequest();
        ReservationProcessingRestriction active = CreateActiveRestriction(request);
        ReservationDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                ReservationProcessingRestrictionReceiptDto>(
                    ReservationsApplicationErrors
                        .ProcessingRestrictionProjectionUnavailable)),
            new NullProjectionRepository(),
            new RecordingRestrictionRepository(active: [active]),
            new TestClock());

        DataRightsRestrictionTargetResolutionResult missing =
            await contributor.ResolveReleaseTargetsAsync(
                CreateTargetRequest(request) with
                {
                    TargetOwnerOperationId = Guid.NewGuid(),
                    TargetOwnerOperationVersion = 1
                },
                CancellationToken.None);
        DataRightsRestrictionTargetResolutionResult stale =
            await contributor.ResolveReleaseTargetsAsync(
                CreateTargetRequest(request) with
                {
                    TargetOwnerOperationId = active.Id,
                    TargetOwnerOperationVersion = active.Version + 1
                },
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionTargetResolutionStatus.NotFound,
            missing.Status);
        Assert.Equal(
            DataRightsRestrictionTargetResolutionStatus.Stale,
            stale.Status);
    }

    [Fact]
    public async Task Tampered_owner_proof_fails_closed()
    {
        DataRightsRestrictionContributionRequest apply = CreateApplyRequest();
        ReservationProcessingRestriction active = CreateActiveRestriction(apply);
        DataRightsRestrictionContributionRequest targeted =
            CreateReleaseRequest(apply, active);
        ReservationProcessingRestrictionReceiptDto tampered = CreateReceiptDto(
            targeted,
            ReservationProcessingRestrictionActionDto.Release,
            restrictionVersion: active.Version + 1,
            projectionRevision: 2,
            effectiveRestricted: false,
            restrictionId: Guid.NewGuid());
        ReservationDataRightsRestrictionContributor tamperedContributor = new(
            new RecordingDispatcher(Result.Success(tampered)),
            new StubProjectionRepository(CreateProjection(targeted, 1)),
            new RecordingRestrictionRepository(active: [active]),
            new TestClock());

        DataRightsRestrictionContributionResult invalidProof =
            await tamperedContributor.ExecuteAsync(
                targeted,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Failed,
            invalidProof.Status);
        Assert.Equal(
            ReservationsApplicationErrors
                .ProcessingRestrictionOwnerProofInvalid.Code,
            invalidProof.OutcomeCode);
    }

    private static DataRightsRestrictionContributionRequest CreateApplyRequest() =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalRevision: 6,
            new DataRightsSubjectCoordinate(
                ReservationsDataRightsCoordinates.Owner,
                ReservationsDataRightsCoordinates.ReservationRecordType,
                Guid.NewGuid(),
                RecordVersion: 3),
            DataRightsRestrictionDirective.Apply,
            "user:executor",
            Now.AddMinutes(1));

    private static DataRightsRestrictionContributionRequest CreateReleaseRequest(
        DataRightsRestrictionContributionRequest request,
        ReservationProcessingRestriction target) =>
        request with
        {
            IdempotencyKey = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            ApprovalRevision = request.ApprovalRevision + 1,
            Directive = DataRightsRestrictionDirective.Release,
            TargetOwnerOperationId = target.Id,
            TargetOwnerOperationVersion = target.Version
        };

    private static DataRightsRestrictionContributionRequest
        CreateLegacyReleaseRequest(
            DataRightsRestrictionContributionRequest request) =>
        request with
        {
            IdempotencyKey = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            ApprovalRevision = request.ApprovalRevision + 1,
            Directive = DataRightsRestrictionDirective.Release,
            TargetOwnerOperationId = null,
            TargetOwnerOperationVersion = null
        };

    private static DataRightsRestrictionTargetResolutionRequest CreateTargetRequest(
        DataRightsRestrictionContributionRequest request) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            request.TenantId,
            request.PropertyId,
            request.CaseId,
            request.Coordinate,
            request.DeadlineUtc);

    private static ReservationProcessingRestrictionProjection CreateProjection(
        DataRightsRestrictionContributionRequest request,
        int activeRestrictionCount = 0)
    {
        ReservationProcessingRestrictionProjection projection =
            ReservationProcessingRestrictionProjection.Create(
                request.TenantId,
                request.PropertyId!.Value,
                request.Coordinate.RecordId,
                ReservationProcessingRestrictionContract.CurrentVersion,
                Now.AddHours(-1)).Value;
        for (int index = 0; index < activeRestrictionCount; index++)
        {
            Assert.True(projection.Apply(
                projection.Revision,
                ReservationProcessingRestrictionContract.CurrentVersion,
                Now.AddMinutes(-30 + index)).IsSuccess);
        }

        return projection;
    }

    private static ReservationProcessingRestriction CreateActiveRestriction(
        DataRightsRestrictionContributionRequest request,
        Guid? applyCaseId = null) =>
        ReservationProcessingRestriction.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            applyCaseId ?? Guid.NewGuid(),
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            request.ExecutingActorId,
            Now.AddMinutes(-10)).Value;

    private static ReservationProcessingRestrictionReceipt CreateReceipt(
        DataRightsRestrictionContributionRequest request,
        ReservationProcessingRestrictionAction action,
        bool effectiveRestricted,
        Guid? restrictionId = null) =>
        ReservationProcessingRestrictionReceipt.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.IdempotencyKey,
            restrictionId ?? Guid.NewGuid(),
            action,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            request.CaseId,
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            ReservationProcessingRestrictionContract.CurrentVersion,
            action == ReservationProcessingRestrictionAction.Apply ? 1 : 2,
            resultingProjectionRevision: 1,
            effectiveRestricted,
            Guid.NewGuid(),
            Now.AddSeconds(-5)).Value;

    private static ReservationProcessingRestrictionReceiptDto CreateReceiptDto(
        DataRightsRestrictionContributionRequest request,
        ReservationProcessingRestrictionActionDto action,
        long restrictionVersion,
        long projectionRevision,
        bool effectiveRestricted,
        Guid? restrictionId = null) =>
        new(
            Guid.NewGuid(),
            restrictionId ?? Guid.NewGuid(),
            action,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            request.CaseId,
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            ReservationProcessingRestrictionContract.CurrentVersion,
            restrictionVersion,
            projectionRevision,
            effectiveRestricted,
            Guid.NewGuid(),
            Now.AddSeconds(-5));

    private sealed class RecordingDispatcher(
        Result<ReservationProcessingRestrictionReceiptDto> result)
        : IRequestDispatcher
    {
        public int CallCount { get; private set; }
        public object? Command { get; private set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            this.Command = command;
            return Task.FromResult((Result<TResponse>)(object)result);
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProjectionRepository(
        ReservationProcessingRestrictionProjection projection)
        : IReservationProcessingRestrictionProjectionRepository
    {
        public Task<ReservationProcessingRestrictionProjection?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReservationProcessingRestrictionProjection?>(
                projection);

        public Task EnsureAsync(
            string tenantId,
            Guid propertyId,
            Guid reservationId,
            DateTimeOffset initializedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullProjectionRepository
        : IReservationProcessingRestrictionProjectionRepository
    {
        public Task<ReservationProcessingRestrictionProjection?> GetAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReservationProcessingRestrictionProjection?>(null);

        public Task EnsureAsync(
            string tenantId,
            Guid propertyId,
            Guid reservationId,
            DateTimeOffset initializedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<ReservationProcessingRestriction>? active = null,
        ReservationProcessingRestrictionReceipt? receipt = null)
        : IReservationProcessingRestrictionRepository
    {
        private readonly IReadOnlyCollection<ReservationProcessingRestriction> active =
            active?.ToArray() ?? [];

        public int ActiveListCount { get; private set; }
        public PageRequest? LastPageRequest { get; private set; }

        public Task<ReservationProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                receipt?.IdempotencyKey == idempotencyKey ? receipt : null);

        public Task<ReservationProcessingRestriction?> FindByApplyApprovalAsync(
            Guid propertyId,
            Guid reservationId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationProcessingRestriction?> FindByReleaseApprovalAsync(
            Guid propertyId,
            Guid reservationId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationProcessingRestriction?> GetAsync(
            Guid propertyId,
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.active.SingleOrDefault(restriction =>
                restriction.PropertyId == propertyId &&
                restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<ReservationProcessingRestriction>>
            ListActiveAsync(
                Guid propertyId,
                Guid reservationId,
                PageRequest pageRequest,
                CancellationToken cancellationToken)
        {
            this.ActiveListCount++;
            this.LastPageRequest = pageRequest;
            return Task.FromResult<
                IReadOnlyCollection<ReservationProcessingRestriction>>(
                    this.active
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
        }

        public Task AddAsync(
            ReservationProcessingRestriction restriction,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            ReservationProcessingRestrictionReceipt value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
