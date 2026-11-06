namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsRestrictionReleaseTargetHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Discovery_is_bounded_and_stably_ordered()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        Guid laterId = Guid.NewGuid();
        Guid earlierId = Guid.NewGuid();
        RecordingContributor contributor = new(request =>
            DataRightsRestrictionTargetResolutionResult.Completed(
                [
                    new(laterId, 3, Guid.NewGuid(), Now.AddMinutes(-1)),
                    new(earlierId, 5, Guid.NewGuid(), Now.AddMinutes(-2))
                ]));
        GetDataRightsRestrictionReleaseTargetsQueryHandler handler = new(
            new StubCaseRepository(dataRightsCase),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<GetDataRightsRestrictionReleaseTargetsQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetDataRightsRestrictionReleaseTargetsQuery(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(dataRightsCase.Version, result.Value.CaseVersion);
        Assert.Equal([earlierId, laterId],
            result.Value.Targets.Select(target => target.OwnerOperationId));
        Assert.Null(contributor.Request?.TargetOwnerOperationId);
    }

    [Fact]
    public async Task Selection_revalidates_the_exact_target_before_mutating_the_case()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        Guid targetId = Guid.NewGuid();
        const long targetVersion = 4;
        RecordingContributor contributor = new(request =>
            DataRightsRestrictionTargetResolutionResult.Completed(
                [new(targetId, targetVersion, Guid.NewGuid(), Now.AddMinutes(-2))]));
        SelectDataRightsRestrictionReleaseTargetCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsRestrictionReleaseTargetCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new SelectDataRightsRestrictionReleaseTargetCommand(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                targetId,
                targetVersion,
                dataRightsCase.Version,
                "user:privacy"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(targetId, result.Value.RestrictionReleaseTarget?.OwnerOperationId);
        Assert.Equal(targetVersion,
            result.Value.RestrictionReleaseTarget?.OwnerOperationVersion);
        Assert.Equal(targetId, contributor.Request?.TargetOwnerOperationId);
        Assert.Equal(targetVersion, contributor.Request?.TargetOwnerOperationVersion);
    }

    [Fact]
    public async Task Review_revalidates_the_selected_target_before_freezing_it()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        Guid targetId = Guid.NewGuid();
        const long targetVersion = 4;
        Assert.True(dataRightsCase.SelectRestrictionReleaseTarget(
            "guests",
            targetId,
            targetVersion,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-2)).IsSuccess);
        RecordingContributor contributor = new(_ =>
            DataRightsRestrictionTargetResolutionResult.Stale());
        RequireDataRightsReviewCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new DataRightsRequiredCompanionExpander(
                [],
                [],
                NullLogger<DataRightsRequiredCompanionExpander>.Instance),
            [contributor],
            new TestClock(),
            NullLogger<RequireDataRightsReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new RequireDataRightsReviewCommand(
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                dataRightsCase.Version,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionReleaseTargetStale,
            result.Error);
        Assert.Equal(
            DataRightsCaseState.Discovery,
            dataRightsCase.Status);
        Assert.Equal(targetId, contributor.Request?.TargetOwnerOperationId);
        Assert.Equal(targetVersion, contributor.Request?.TargetOwnerOperationVersion);
    }

    [Fact]
    public void Exact_resolution_fails_closed_on_a_substituted_target()
    {
        Guid requestedId = Guid.NewGuid();

        var result = DataRightsRestrictionTargetResolution.Validate(
            DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), Now)]),
            Now,
            requestedId,
            requestedOperationVersion: 2);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionReleaseTargetResultInvalid,
            result.Error);
    }

    [Fact]
    public void Explicit_target_states_preserve_retry_and_stale_semantics()
    {
        Guid targetId = Guid.NewGuid();

        var stale = DataRightsRestrictionTargetResolution.Validate(
            DataRightsRestrictionTargetResolutionResult.Stale(),
            Now,
            targetId,
            requestedOperationVersion: 2);
        var retry = DataRightsRestrictionTargetResolution.Validate(
            DataRightsRestrictionTargetResolutionResult.Failed("owner.timeout"),
            Now);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionReleaseTargetStale,
            stale.Error);
        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            retry.Error);
    }

    [Fact]
    public void Resolution_rejects_an_owner_timestamp_after_observation()
    {
        var result = DataRightsRestrictionTargetResolution.Validate(
            DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), Now.AddSeconds(1))]),
            Now);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionReleaseTargetResultInvalid,
            result.Error);
    }

    [Fact]
    public async Task Resolution_accepts_an_owner_timestamp_observed_during_call()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        DateTimeOffset observedAtUtc = Now.AddSeconds(1);
        MutableClock clock = new(Now);
        RecordingContributor contributor = new(_ =>
        {
            clock.UtcNow = observedAtUtc;
            return DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), observedAtUtc)]);
        });

        var result = await GetDataRightsRestrictionReleaseTargetsQueryHandler
            .ResolveAsync(
                contributor,
                dataRightsCase,
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.SelectedSubjects.Single(),
                targetId: null,
                targetVersion: null,
                Now.AddSeconds(30),
                clock,
                NullLogger.Instance,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(observedAtUtc, result.Value.Targets.Single().AppliedAtUtc);
    }

    [Fact]
    public async Task Resolution_enforces_the_owner_deadline()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        WaitingContributor contributor = new();

        var result = await GetDataRightsRestrictionReleaseTargetsQueryHandler.ResolveAsync(
            contributor,
            dataRightsCase,
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.SelectedSubjects.Single(),
            targetId: null,
            targetVersion: null,
            Now.AddMilliseconds(25),
            new TestClock(),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
        Assert.True(contributor.CancellationObserved);
    }

    [Fact]
    public async Task Resolution_rejects_a_normal_return_after_deadline_cancellation()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        CancellationIgnoringContributor contributor = new();

        var result = await GetDataRightsRestrictionReleaseTargetsQueryHandler
            .ResolveAsync(
                contributor,
                dataRightsCase,
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.SelectedSubjects.Single(),
                targetId: null,
                targetVersion: null,
                Now.AddMilliseconds(25),
                new TestClock(),
                NullLogger.Instance,
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
        Assert.True(contributor.CancellationObserved);
    }

    [Fact]
    public async Task Resolution_preserves_caller_cancellation()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        WaitingContributor contributor = new();
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GetDataRightsRestrictionReleaseTargetsQueryHandler.ResolveAsync(
                contributor,
                dataRightsCase,
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.SelectedSubjects.Single(),
                targetId: null,
                targetVersion: null,
                Now.AddMinutes(1),
                new TestClock(),
                NullLogger.Instance,
                cancellation.Token));

        Assert.True(contributor.CancellationObserved);
    }

    [Fact]
    public async Task Resolution_returned_at_deadline_requests_retry()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        MutableClock clock = new(Now);
        RecordingContributor contributor = new(request =>
        {
            clock.UtcNow = request.DeadlineUtc;
            return DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), Now.AddMinutes(-1))]);
        });

        var result = await GetDataRightsRestrictionReleaseTargetsQueryHandler
            .ResolveAsync(
                contributor,
                dataRightsCase,
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.SelectedSubjects.Single(),
                targetId: null,
                targetVersion: null,
                Now.AddSeconds(30),
                clock,
                NullLogger.Instance,
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
    }

    [Fact]
    public async Task Resolution_rejects_normal_return_after_caller_cancellation()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        using CancellationTokenSource cancellation = new();
        RecordingContributor contributor = new(_ =>
        {
            cancellation.Cancel();
            return DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), Now.AddMinutes(-1))]);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GetDataRightsRestrictionReleaseTargetsQueryHandler.ResolveAsync(
                contributor,
                dataRightsCase,
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.SelectedSubjects.Single(),
                targetId: null,
                targetVersion: null,
                Now.AddSeconds(30),
                new TestClock(),
                NullLogger.Instance,
                cancellation.Token));
    }

    private static DataRightsCase CreateDiscoveryCase()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsRestrictionAction.Release).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            Now.AddMinutes(-5)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-3)).IsSuccess);
        return dataRightsCase;
    }

    private sealed class RecordingContributor(
        Func<DataRightsRestrictionTargetResolutionRequest,
            DataRightsRestrictionTargetResolutionResult> resolve)
        : IDataRightsRestrictionContributor
    {
        public string OwnerKey => "guests";
        public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;
        public DataRightsRestrictionTargetResolutionRequest? Request { get; private set; }

        public Task<DataRightsRestrictionTargetResolutionResult>
            ResolveReleaseTargetsAsync(
                DataRightsRestrictionTargetResolutionRequest request,
                CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(resolve(request));
        }

        public Task<DataRightsRestrictionContributionResult> ExecuteAsync(
            DataRightsRestrictionContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class WaitingContributor : IDataRightsRestrictionContributor
    {
        public string OwnerKey => "guests";
        public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;
        public bool CancellationObserved { get; private set; }

        public async Task<DataRightsRestrictionTargetResolutionResult>
            ResolveReleaseTargetsAsync(
                DataRightsRestrictionTargetResolutionRequest request,
                CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DataRightsRestrictionTargetResolutionResult.Failed(
                    "unexpected.completion");
            }
            finally
            {
                this.CancellationObserved = cancellationToken.IsCancellationRequested;
            }
        }

        public Task<DataRightsRestrictionContributionResult> ExecuteAsync(
            DataRightsRestrictionContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationIgnoringContributor
        : IDataRightsRestrictionContributor
    {
        public string OwnerKey => "guests";
        public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;
        public bool CancellationObserved { get; private set; }

        public async Task<DataRightsRestrictionTargetResolutionResult>
            ResolveReleaseTargetsAsync(
                DataRightsRestrictionTargetResolutionRequest request,
                CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(1),
                    CancellationToken.None);
            }

            this.CancellationObserved = true;
            return DataRightsRestrictionTargetResolutionResult.Completed(
                [new(Guid.NewGuid(), 2, Guid.NewGuid(), Now.AddMinutes(-1))]);
        }

        public Task<DataRightsRestrictionContributionResult> ExecuteAsync(
            DataRightsRestrictionContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DataRightsCase?>(
                dataRightsCase.Id == caseId &&
                dataRightsCase.PropertyId == scope.PropertyId &&
                dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType
                    ? dataRightsCase
                    : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => "tenant-a";

        public IDisposable Push(string? requestedScopeId) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
