namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffOnboardingDataRightsRestrictionContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Apply_binds_exact_coordinate_and_returns_owner_proof()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Apply);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(request);
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto receipt =
            CreateReceiptDto(
                request,
                WorkspaceStaffOnboardingProcessingRestrictionActionDto.Apply,
                restrictionVersion: 1,
                projectionRevision: 1,
                effectiveRestricted: true);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
                dispatcher,
                new StubProjectionRepository(projection),
                new RecordingRestrictionRepository(),
                new TestClock());

        DataRightsRestrictionContributionResult result =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(WorkspacesDataRightsCoordinates.Owner, contributor.OwnerKey);
        Assert.Equal(
            DataRightsRestrictionContract.CurrentVersion,
            contributor.ContractVersion);
        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            result.Status);
        Assert.Null(result.OutcomeCode);
        DataRightsRestrictionOwnerProof proof = Assert.IsType<
            DataRightsRestrictionOwnerProof>(result.OwnerProof);
        Assert.Equal(receipt.ReceiptId, proof.ReceiptId);
        Assert.Equal(receipt.RestrictionId, proof.OwnerOperationId);
        Assert.Equal(receipt.RestrictionVersion, proof.ResultingOwnerRevision);
        Assert.Equal(
            receipt.ProjectionRevision,
            proof.ResultingProjectionRevision);
        Assert.True(proof.EffectiveRestricted);
        Assert.Equal(DataRightsRestrictionContract.Sha256Length,
            proof.ReceiptSha256.Length);

        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommand command =
            Assert.IsType<
                ApplyWorkspaceStaffOnboardingProcessingRestrictionCommand>(
                dispatcher.LastCommand);
        Assert.Equal(request.IdempotencyKey, command.IdempotencyKey);
        Assert.Equal(request.CaseId, command.CaseId);
        Assert.Equal(request.ApprovalRevision, command.ApprovalRevision);
        Assert.Equal(request.Coordinate.RecordId, command.ApplicationId);
        Assert.Equal(
            request.Coordinate.RecordVersion,
            command.ExpectedOnboardingVersion);
        Assert.Equal(projection.Revision, command.ExpectedProjectionRevision);
        Assert.Equal(request.ExecutingActorId, command.ActorId);
    }

    [Fact]
    public async Task Committed_receipt_replays_exact_proof_without_state_lookup()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Apply);
        WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt =
            CreateReceipt(
                request,
                WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                effectiveRestricted: true);
        RecordingRestrictionRepository repository = new(receipt: receipt);
        RecordingDispatcher dispatcher = new(Result.Failure<
            WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable));
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
                dispatcher,
                new NullProjectionRepository(),
                repository,
                new TestClock());

        DataRightsRestrictionContributionResult first =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);
        DataRightsRestrictionContributionResult replay =
            await contributor.ExecuteAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionContributionStatus.Completed,
            first.Status);
        Assert.Equal(first.OwnerProof, replay.OwnerProof);
        Assert.NotNull(first.OwnerProof);
        Assert.Equal(receipt.Id, first.OwnerProof.ReceiptId);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(0, repository.ActiveListCount);
    }

    [Fact]
    public async Task Release_with_multiple_active_restrictions_fails_closed()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Release);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(request);
        WorkspaceStaffOnboardingProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        RecordingDispatcher dispatcher = new(Result.Failure<
            WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable));
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
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
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionActiveStateInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task
        Targeted_release_dispatches_one_of_multiple_active_restrictions()
    {
        DataRightsRestrictionContributionRequest request =
            CreateRequest(DataRightsRestrictionDirective.Release);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(request);
        WorkspaceStaffOnboardingProcessingRestriction[] active =
        [
            CreateActiveRestriction(request),
            CreateActiveRestriction(request)
        ];
        WorkspaceStaffOnboardingProcessingRestriction selected = active[1];
        request = request with
        {
            TargetOwnerOperationId = selected.Id,
            TargetOwnerOperationVersion = selected.Version
        };
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto receipt =
            CreateReceiptDto(
                request,
                WorkspaceStaffOnboardingProcessingRestrictionActionDto
                    .Release,
                restrictionVersion: selected.Version + 1,
                projectionRevision: projection.Revision + 1,
                effectiveRestricted: true,
                restrictionId: selected.Id);
        RecordingDispatcher dispatcher = new(Result.Success(receipt));
        RecordingRestrictionRepository repository = new(active: active);
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
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
        ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand command =
            Assert.IsType<
                ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand>(
                dispatcher.LastCommand);
        Assert.False(command.LegacyUnboundTarget);
    }

    [Fact]
    public async Task Release_target_discovery_is_bounded_with_lookahead()
    {
        DataRightsRestrictionContributionRequest execution =
            CreateRequest(DataRightsRestrictionDirective.Release);
        WorkspaceStaffOnboardingProcessingRestriction[] active =
            Enumerable.Range(
                    0,
                    DataRightsRestrictionContract.MaxReleaseTargets + 1)
                .Select(_ => CreateActiveRestriction(execution))
                .ToArray();
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
                new RecordingDispatcher(Result.Failure<
                    WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .RestrictionProjectionUnavailable)),
                new NullProjectionRepository(),
                new RecordingRestrictionRepository(active: active),
                new TestClock());

        DataRightsRestrictionTargetResolutionResult result =
            await contributor.ResolveReleaseTargetsAsync(
                CreateTargetRequest(execution),
                CancellationToken.None);

        Assert.Equal(
            DataRightsRestrictionTargetResolutionStatus.Completed,
            result.Status);
        Assert.Equal(
            DataRightsRestrictionContract.MaxReleaseTargets,
            result.Targets?.Count);
        Assert.True(result.LimitReached);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("record")]
    [InlineData("case")]
    [InlineData("property")]
    [InlineData("deadline")]
    public async Task Non_exact_workspaces_staff_onboarding_request_is_rejected(
        string invalidPart)
    {
        DataRightsRestrictionContributionRequest request =
            MakeInvalid(
                CreateRequest(DataRightsRestrictionDirective.Apply),
                invalidPart);
        RecordingDispatcher dispatcher = new(Result.Failure<
            WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable));
        WorkspaceStaffOnboardingDataRightsRestrictionContributor contributor =
            new(
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
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionRequestInvalid.Code,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.CallCount);
    }

    private static DataRightsRestrictionContributionRequest MakeInvalid(
        DataRightsRestrictionContributionRequest request,
        string invalidPart) =>
        invalidPart switch
        {
            "owner" => request with
            {
                Coordinate = request.Coordinate with
                {
                    OwnerKey =
                        WorkspacesDataRightsCoordinates.Owner.ToUpperInvariant()
                }
            },
            "record" => request with
            {
                Coordinate = request.Coordinate with
                {
                    RecordType =
                        WorkspacesDataRightsCoordinates
                            .StaffOnboardingRecordType.ToUpperInvariant()
                }
            },
            "case" => request with
            {
                CaseType = DataRightsCaseType.GuestRights
            },
            "property" => request with
            {
                PropertyId = Guid.NewGuid()
            },
            "deadline" => request with
            {
                DeadlineUtc = Now
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(invalidPart),
                invalidPart,
                "Unknown invalid request part.")
        };

    private static DataRightsRestrictionContributionRequest CreateRequest(
        DataRightsRestrictionDirective directive) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            "tenant-a",
            Guid.NewGuid(),
            PropertyId: null,
            Guid.NewGuid(),
            ApprovalRevision: 7,
            new DataRightsSubjectCoordinate(
                WorkspacesDataRightsCoordinates.Owner,
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                Guid.NewGuid(),
                RecordVersion: 3),
            directive,
            "user:privacy",
            Now.AddMinutes(2),
            DataRightsCaseType.StaffRights);

    private static DataRightsRestrictionTargetResolutionRequest
        CreateTargetRequest(
            DataRightsRestrictionContributionRequest request) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            request.TenantId,
            request.PropertyId,
            request.CaseId,
            request.Coordinate,
            request.DeadlineUtc,
            request.CaseType);

    private static
        WorkspaceStaffOnboardingProcessingRestrictionProjection
        CreateProjection(DataRightsRestrictionContributionRequest request) =>
        WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
            request.TenantId,
            request.Coordinate.RecordId,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            Now.AddHours(-1)).Value;

    private static WorkspaceStaffOnboardingProcessingRestriction
        CreateActiveRestriction(
            DataRightsRestrictionContributionRequest request) =>
        WorkspaceStaffOnboardingProcessingRestriction.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.Coordinate.RecordId,
            Guid.NewGuid(),
            request.ApprovalRevision - 1,
            request.Coordinate.RecordVersion,
            "user:privacy",
            Now.AddMinutes(-10)).Value;

    private static WorkspaceStaffOnboardingProcessingRestrictionReceipt
        CreateReceipt(
            DataRightsRestrictionContributionRequest request,
            WorkspaceStaffOnboardingProcessingRestrictionAction action,
            bool effectiveRestricted) =>
        WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
            Guid.NewGuid(),
            request.TenantId,
            request.IdempotencyKey,
            Guid.NewGuid(),
            action,
            request.Coordinate.RecordId,
            request.CaseId,
            request.ApprovalRevision,
            request.Coordinate.RecordVersion,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            action ==
                WorkspaceStaffOnboardingProcessingRestrictionAction.Apply
                ? 1
                : 2,
            resultingProjectionRevision: 1,
            effectiveRestricted,
            request.ExecutingActorId,
            Guid.NewGuid(),
            Now.AddSeconds(-5)).Value;

    private static
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto
        CreateReceiptDto(
            DataRightsRestrictionContributionRequest request,
            WorkspaceStaffOnboardingProcessingRestrictionActionDto action,
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
        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto> result)
        : IRequestDispatcher
    {
        public int CallCount { get; private set; }

        public object? LastCommand { get; private set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            this.LastCommand = command;
            return Task.FromResult((Result<TResponse>)(object)result);
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProjectionRepository(
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection)
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<
                WorkspaceStaffOnboardingProcessingRestrictionProjection?>(
                projection);

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullProjectionRepository
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<
                WorkspaceStaffOnboardingProcessingRestrictionProjection?>(null);

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<WorkspaceStaffOnboardingProcessingRestriction>? active =
            null,
        WorkspaceStaffOnboardingProcessingRestrictionReceipt? receipt = null)
        : IWorkspaceStaffOnboardingProcessingRestrictionRepository
    {
        private readonly IReadOnlyCollection<
            WorkspaceStaffOnboardingProcessingRestriction> active =
            active?.ToArray() ?? [];

        public int ActiveListCount { get; private set; }

        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                receipt?.IdempotencyKey == idempotencyKey
                    ? receipt
                    : null);

        public Task<WorkspaceStaffOnboardingProcessingRestriction?>
            FindByApplyApprovalAsync(
                Guid applicationId,
                Guid caseId,
                long approvalRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboardingProcessingRestriction?>
            FindByReleaseApprovalAsync(
                Guid applicationId,
                Guid caseId,
                long approvalRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboardingProcessingRestriction?> GetAsync(
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.active.SingleOrDefault(
                    restriction => restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<
            WorkspaceStaffOnboardingProcessingRestriction>> ListActiveAsync(
            Guid applicationId,
            PageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            this.ActiveListCount++;
            return Task.FromResult<IReadOnlyCollection<
                WorkspaceStaffOnboardingProcessingRestriction>>(
                this.active
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());
        }

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestriction restriction,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            WorkspaceStaffOnboardingProcessingRestrictionReceipt added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
