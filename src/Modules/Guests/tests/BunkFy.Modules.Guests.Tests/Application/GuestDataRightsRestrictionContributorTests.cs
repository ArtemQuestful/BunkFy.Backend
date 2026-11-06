namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestDataRightsRestrictionContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Committed_owner_receipt_replays_before_current_state_lookup()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Apply);
        GuestProcessingRestrictionReceipt receipt = CreateReceipt(
            request,
            GuestProcessingRestrictionAction.Apply,
            effectiveRestricted: true);
        RecordingRestrictionRepository repository = new(receipt: receipt);
        RecordingDispatcher dispatcher = new(Result.Failure<
            GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionProjectionUnavailable));
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new NullProjectionRepository(),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Completed, result.Status);
        Assert.NotNull(result.OwnerProof);
        Assert.Equal(receipt.Id, result.OwnerProof.ReceiptId);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(0, repository.ActiveListCount);
    }

    [Fact]
    public async Task Apply_dispatches_even_when_another_case_is_active()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Apply);
        GuestProcessingRestrictionProjection projection = CreateProjection(request);
        GuestProcessingRestriction active = CreateActiveRestriction(request);
        GuestProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            GuestProcessingRestrictionActionDto.Apply,
            restrictionVersion: 1,
            projectionRevision: 2,
            effectiveRestricted: true);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(active: [active]),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Completed, result.Status);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public async Task Release_with_multiple_active_restrictions_fails_closed()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Release);
        GuestProcessingRestrictionProjection projection = CreateProjection(request);
        GuestProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        RecordingDispatcher dispatcher = new(Result.Failure<
            GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionProjectionUnavailable));
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(active: active),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Blocked, result.Status);
        Assert.Equal(
            GuestsApplicationErrors.RestrictionActiveStateInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task Targeted_release_dispatches_one_of_multiple_active_restrictions()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Release);
        GuestProcessingRestrictionProjection projection = CreateProjection(request);
        GuestProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        GuestProcessingRestriction selected = active[1];
        request = request with
        {
            TargetOwnerOperationId = selected.Id,
            TargetOwnerOperationVersion = selected.Version
        };
        GuestProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            GuestProcessingRestrictionActionDto.Release,
            restrictionVersion: selected.Version + 1,
            projectionRevision: projection.Revision + 1,
            effectiveRestricted: true,
            restrictionId: selected.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        RecordingRestrictionRepository repository = new(active: active);
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Completed, result.Status);
        Assert.True(result.OwnerProof?.EffectiveRestricted);
        Assert.Equal(selected.Id, result.OwnerProof?.OwnerOperationId);
        Assert.Equal(0, repository.ActiveListCount);
        var command = Assert.IsType<
            BunkFy.Modules.Guests.Application.Commands
                .ReleaseGuestProcessingRestrictionCommand>(dispatcher.Command);
        Assert.False(command.LegacyUnboundTarget);
    }

    [Fact]
    public async Task Release_target_discovery_is_bounded_with_lookahead()
    {
        DataRightsRestrictionContributionRequest execution = CreateRequest(
            DataRightsRestrictionDirective.Release);
        GuestProcessingRestriction[] active = Enumerable.Range(
                0,
                DataRightsRestrictionContract.MaxReleaseTargets + 1)
            .Select(_ => CreateActiveRestriction(execution))
            .ToArray();
        GuestDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                GuestProcessingRestrictionReceiptDto>(
                    GuestsApplicationErrors.RestrictionProjectionUnavailable)),
            new NullProjectionRepository(),
            new RecordingRestrictionRepository(active: active),
            new TestClock());

        DataRightsRestrictionTargetResolutionResult result =
            await contributor.ResolveReleaseTargetsAsync(
                CreateTargetRequest(execution),
                CancellationToken.None);

        Assert.Equal(DataRightsRestrictionTargetResolutionStatus.Completed, result.Status);
        Assert.Equal(DataRightsRestrictionContract.MaxReleaseTargets, result.Targets?.Count);
        Assert.True(result.LimitReached);
    }

    [Fact]
    public async Task Release_with_one_active_restriction_dispatches_owner_command()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Release);
        GuestProcessingRestrictionProjection projection = CreateProjection(request);
        GuestProcessingRestriction active = CreateActiveRestriction(request);
        GuestProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            GuestProcessingRestrictionActionDto.Release,
            restrictionVersion: active.Version + 1,
            projectionRevision: projection.Revision + 1,
            effectiveRestricted: false,
            restrictionId: active.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(active: [active]),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Completed, result.Status);
        Assert.False(result.OwnerProof?.EffectiveRestricted);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public async Task Release_without_an_active_restriction_fails_closed()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Release);
        RecordingDispatcher dispatcher = new(Result.Failure<
            GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionProjectionUnavailable));
        GuestDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(CreateProjection(request)),
            new RecordingRestrictionRepository(),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Blocked, result.Status);
        Assert.Equal(
            GuestsApplicationErrors.RestrictionActiveStateInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task Tampered_owner_result_fails_proof_validation()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Apply);
        GuestProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            GuestProcessingRestrictionActionDto.Apply,
            restrictionVersion: 1,
            projectionRevision: 1,
            effectiveRestricted: false);
        GuestDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Success(receipt)),
            new StubProjectionRepository(CreateProjection(request)),
            new RecordingRestrictionRepository(),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Failed, result.Status);
        Assert.Equal(
            GuestsApplicationErrors.RestrictionOwnerProofInvalid.Code,
            result.OutcomeCode);
    }

    [Fact]
    public async Task Mismatched_replay_receipt_fails_closed()
    {
        DataRightsRestrictionContributionRequest request = CreateRequest(
            DataRightsRestrictionDirective.Apply);
        GuestProcessingRestrictionReceipt receipt = CreateReceipt(
            request with { CaseId = Guid.NewGuid() },
            GuestProcessingRestrictionAction.Apply,
            effectiveRestricted: true);
        GuestDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                GuestProcessingRestrictionReceiptDto>(
                    GuestsApplicationErrors.RestrictionProjectionUnavailable)),
            new NullProjectionRepository(),
            new RecordingRestrictionRepository(receipt: receipt),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(DataRightsRestrictionContributionStatus.Failed, result.Status);
        Assert.Equal(
            GuestsApplicationErrors.RestrictionOwnerProofInvalid.Code,
            result.OutcomeCode);
    }

    private static DataRightsRestrictionContributionRequest CreateRequest(
        DataRightsRestrictionDirective directive) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            6,
            new DataRightsSubjectCoordinate(
                GuestsDataRightsCoordinates.Owner,
                GuestsDataRightsCoordinates.GuestProfileRecordType,
                Guid.NewGuid(),
                RecordVersion: 3),
            directive,
            "user:executor",
            Now.AddMinutes(1));

    private static DataRightsRestrictionTargetResolutionRequest CreateTargetRequest(
        DataRightsRestrictionContributionRequest request) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            request.TenantId,
            request.PropertyId,
            request.CaseId,
            request.Coordinate,
            request.DeadlineUtc);

    private static GuestProcessingRestrictionProjection CreateProjection(
        DataRightsRestrictionContributionRequest request) =>
        GuestProcessingRestrictionProjection.Create(
            request.TenantId,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            GuestProcessingRestrictionContract.CurrentVersion,
            Now.AddHours(-1)).Value;

    private static GuestProcessingRestriction CreateActiveRestriction(
        DataRightsRestrictionContributionRequest request) =>
        GuestProcessingRestriction.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            Guid.NewGuid(),
            request.ApprovalRevision - 1,
            request.Coordinate.RecordVersion,
            "user:privacy",
            Now.AddMinutes(-10)).Value;

    private static GuestProcessingRestrictionReceipt CreateReceipt(
        DataRightsRestrictionContributionRequest request,
        GuestProcessingRestrictionAction action,
        bool effectiveRestricted) =>
        GuestProcessingRestrictionReceipt.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.IdempotencyKey,
            Guid.NewGuid(),
            action,
            request.PropertyId!.Value,
            request.Coordinate.RecordId,
            request.CaseId,
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            GuestProcessingRestrictionContract.CurrentVersion,
            action == GuestProcessingRestrictionAction.Apply ? 1 : 2,
            resultingProjectionRevision: 1,
            effectiveRestricted,
            request.ExecutingActorId,
            Guid.NewGuid(),
            Now.AddSeconds(-5)).Value;

    private static GuestProcessingRestrictionReceiptDto CreateReceiptDto(
        DataRightsRestrictionContributionRequest request,
        GuestProcessingRestrictionActionDto action,
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
            restrictionVersion,
            projectionRevision,
            effectiveRestricted,
            request.ExecutingActorId,
            Guid.NewGuid(),
            Now.AddSeconds(-5));

    private sealed class RecordingDispatcher(
        Result<GuestProcessingRestrictionReceiptDto> result)
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
        GuestProcessingRestrictionProjection projection)
        : IGuestProcessingRestrictionProjectionRepository
    {
        public Task<GuestProcessingRestrictionProjection?> GetAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) =>
            Task.FromResult<GuestProcessingRestrictionProjection?>(projection);

        public Task EnsureAsync(
            string tenantId,
            Guid propertyId,
            Guid guestId,
            DateTimeOffset initializedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullProjectionRepository
        : IGuestProcessingRestrictionProjectionRepository
    {
        public Task<GuestProcessingRestrictionProjection?> GetAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) =>
            Task.FromResult<GuestProcessingRestrictionProjection?>(null);

        public Task EnsureAsync(
            string tenantId,
            Guid propertyId,
            Guid guestId,
            DateTimeOffset initializedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<GuestProcessingRestriction>? active = null,
        GuestProcessingRestrictionReceipt? receipt = null)
        : IGuestProcessingRestrictionRepository
    {
        private readonly IReadOnlyCollection<GuestProcessingRestriction> active =
            active?.ToArray() ?? [];

        public int ActiveListCount { get; private set; }

        public Task<GuestProcessingRestrictionReceipt?> FindReceiptByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                receipt?.IdempotencyKey == idempotencyKey ? receipt : null);

        public Task<GuestProcessingRestriction?> FindByApplyApprovalAsync(
            Guid propertyId,
            Guid guestId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestProcessingRestriction?> FindByReleaseApprovalAsync(
            Guid propertyId,
            Guid guestId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestProcessingRestriction?> GetAsync(
            Guid propertyId,
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.active.SingleOrDefault(restriction =>
                restriction.PropertyId == propertyId &&
                restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<GuestProcessingRestriction>> ListActiveAsync(
            Guid propertyId,
            Guid guestId,
            PageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            this.ActiveListCount++;
            return Task.FromResult<IReadOnlyCollection<GuestProcessingRestriction>>(
                this.active
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());
        }

        public Task AddAsync(
            GuestProcessingRestriction restriction,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            GuestProcessingRestrictionReceipt value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
