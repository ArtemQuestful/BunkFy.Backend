namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffDataRightsRestrictionContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Committed_owner_receipt_replays_before_current_state_lookup()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Apply);
        StaffProcessingRestrictionReceipt receipt = CreateReceipt(
            request,
            StaffProcessingRestrictionAction.Apply,
            effectiveRestricted: true);
        RecordingRestrictionRepository repository =
            new(receipt: receipt);
        RecordingDispatcher dispatcher = new(Result.Failure<
            StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionProjectionUnavailable));
        StaffDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new NullProjectionRepository(),
            repository,
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        Assert.NotNull(result.OwnerProof);
        Assert.Equal(receipt.Id, result.OwnerProof.ReceiptId);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(0, repository.ActiveListCount);
    }

    [Fact]
    public async Task Apply_dispatches_even_when_another_case_is_active()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Apply);
        StaffProcessingRestrictionProjection projection =
            CreateProjection(request);
        StaffProcessingRestriction active =
            CreateActiveRestriction(request);
        StaffProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            StaffProcessingRestrictionActionDto.Apply,
            restrictionVersion: 1,
            projectionRevision: 2,
            effectiveRestricted: true);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        StaffDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(active: [active]),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        Assert.Equal(1, dispatcher.CallCount);
        Assert.True(result.OwnerProof?.EffectiveRestricted);
    }

    [Fact]
    public async Task Release_with_multiple_active_restrictions_fails_closed()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Release);
        StaffProcessingRestrictionProjection projection =
            CreateProjection(request);
        StaffProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        RecordingDispatcher dispatcher = new(Result.Failure<
            StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionProjectionUnavailable));
        StaffDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new StubProjectionRepository(projection),
            new RecordingRestrictionRepository(active: active),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Blocked,
            result.Status);
        Assert.Equal(
            StaffApplicationErrors.RestrictionActiveStateInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task Targeted_release_dispatches_one_of_multiple_active_restrictions()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Release);
        StaffProcessingRestrictionProjection projection = CreateProjection(request);
        StaffProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        StaffProcessingRestriction selected = active[1];
        request = request with
        {
            TargetOwnerOperationId = selected.Id,
            TargetOwnerOperationVersion = selected.Version
        };
        StaffProcessingRestrictionReceiptDto receipt = CreateReceiptDto(
            request,
            StaffProcessingRestrictionActionDto.Release,
            restrictionVersion: selected.Version + 1,
            projectionRevision: projection.Revision + 1,
            effectiveRestricted: true,
            restrictionId: selected.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        RecordingRestrictionRepository repository = new(active: active);
        StaffDataRightsRestrictionContributor contributor = new(
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
            BunkFy.Modules.Staff.Application.Commands
                .ReleaseStaffProcessingRestrictionCommand>(dispatcher.Command);
        Assert.False(command.LegacyUnboundTarget);
    }

    [Fact]
    public async Task Release_target_discovery_is_bounded_with_lookahead()
    {
        DataRightsRestrictionContributionRequest execution =
            CreateRequest(DataRightsRestrictionDirective.Release);
        StaffProcessingRestriction[] active = Enumerable.Range(
                0,
                DataRightsRestrictionContract.MaxReleaseTargets + 1)
            .Select(_ => CreateActiveRestriction(execution))
            .ToArray();
        StaffDataRightsRestrictionContributor contributor = new(
            new RecordingDispatcher(Result.Failure<
                StaffProcessingRestrictionReceiptDto>(
                    StaffApplicationErrors.RestrictionProjectionUnavailable)),
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Non_staff_or_property_scoped_request_is_rejected(
        bool useGuestCase,
        bool usePropertyScope)
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Apply) with
            {
                CaseType = useGuestCase
                    ? DataRightsCaseType.GuestRights
                    : DataRightsCaseType.StaffRights,
                PropertyId = usePropertyScope ? Guid.NewGuid() : null
            };
        RecordingDispatcher dispatcher = new(Result.Failure<
            StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionProjectionUnavailable));
        StaffDataRightsRestrictionContributor contributor = new(
            dispatcher,
            new NullProjectionRepository(),
            new RecordingRestrictionRepository(),
            new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Failed,
            result.Status);
        Assert.Equal(
            StaffApplicationErrors.RestrictionRequestInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    private static DataRightsRestrictionContributionRequest CreateRequest(
        DataRightsRestrictionDirective directive) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            "tenant-a",
            Guid.NewGuid(),
            PropertyId: null,
            Guid.NewGuid(),
            6,
            new DataRightsSubjectCoordinate(
                StaffDataRightsCoordinates.Owner,
                StaffDataRightsCoordinates.StaffMemberRecordType,
                Guid.NewGuid(),
                RecordVersion: 3),
            directive,
            "user:executor",
            Now.AddMinutes(1),
            DataRightsCaseType.StaffRights);

    private static StaffProcessingRestrictionProjection CreateProjection(
        DataRightsRestrictionContributionRequest request) =>
        StaffProcessingRestrictionProjection.Create(
            request.TenantId,
            request.Coordinate.RecordId,
            StaffProcessingRestrictionContract.CurrentVersion,
            Now.AddHours(-1)).Value;

    private static DataRightsRestrictionTargetResolutionRequest CreateTargetRequest(
        DataRightsRestrictionContributionRequest request) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            request.TenantId,
            request.PropertyId,
            request.CaseId,
            request.Coordinate,
            request.DeadlineUtc,
            DataRightsCaseType.StaffRights);

    private static StaffProcessingRestriction CreateActiveRestriction(
        DataRightsRestrictionContributionRequest request) =>
        StaffProcessingRestriction.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.Coordinate.RecordId,
            Guid.NewGuid(),
            request.ApprovalRevision - 1,
            request.Coordinate.RecordVersion,
            "user:privacy",
            Now.AddMinutes(-10)).Value;

    private static StaffProcessingRestrictionReceipt CreateReceipt(
        DataRightsRestrictionContributionRequest request,
        StaffProcessingRestrictionAction action,
        bool effectiveRestricted) =>
        StaffProcessingRestrictionReceipt.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.IdempotencyKey,
            Guid.NewGuid(),
            action,
            request.Coordinate.RecordId,
            request.CaseId,
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            StaffProcessingRestrictionContract.CurrentVersion,
            action == StaffProcessingRestrictionAction.Apply ? 1 : 2,
            resultingProjectionRevision: 1,
            effectiveRestricted,
            request.ExecutingActorId,
            Guid.NewGuid(),
            Now.AddSeconds(-5)).Value;

    private static StaffProcessingRestrictionReceiptDto CreateReceiptDto(
        DataRightsRestrictionContributionRequest request,
        StaffProcessingRestrictionActionDto action,
        long restrictionVersion,
        long projectionRevision,
        bool effectiveRestricted,
        Guid? restrictionId = null) =>
        new(
            Guid.NewGuid(),
            restrictionId ?? Guid.NewGuid(),
            action,
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
        Result<StaffProcessingRestrictionReceiptDto> result)
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
        StaffProcessingRestrictionProjection projection)
        : IStaffProcessingRestrictionProjectionRepository
    {
        public Task<StaffProcessingRestrictionProjection?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffProcessingRestrictionProjection?>(
                projection);
    }

    private sealed class NullProjectionRepository
        : IStaffProcessingRestrictionProjectionRepository
    {
        public Task<StaffProcessingRestrictionProjection?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffProcessingRestrictionProjection?>(null);
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<StaffProcessingRestriction>? active = null,
        StaffProcessingRestrictionReceipt? receipt = null)
        : IStaffProcessingRestrictionRepository
    {
        private readonly IReadOnlyCollection<StaffProcessingRestriction> active =
            active?.ToArray() ?? [];

        public int ActiveListCount { get; private set; }

        public Task<StaffProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                receipt?.IdempotencyKey == idempotencyKey
                    ? receipt
                    : null);

        public Task<StaffProcessingRestriction?> FindByApplyApprovalAsync(
            Guid staffMemberId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffProcessingRestriction?> FindByReleaseApprovalAsync(
            Guid staffMemberId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffProcessingRestriction?> GetAsync(
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.active.SingleOrDefault(restriction =>
                restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<StaffProcessingRestriction>>
            ListActiveAsync(
                Guid staffMemberId,
                PageRequest pageRequest,
                CancellationToken cancellationToken)
        {
            this.ActiveListCount++;
            return Task.FromResult<
                IReadOnlyCollection<StaffProcessingRestriction>>(
                this.active
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());
        }

        public Task AddAsync(
            StaffProcessingRestriction restriction,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            StaffProcessingRestrictionReceipt value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
