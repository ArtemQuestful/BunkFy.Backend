namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestRetentionContributorTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Mutation_cap_resumes_before_first_deferred_guest()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot[] candidates =
            Enumerable.Range(1, 4)
                .Select(ordinal => GuestRetentionTestData.Snapshot(
                    policy.Binding,
                    GuestStayStatus.CheckedOut,
                    new DateOnly(2025, 1, 1),
                    projectionOrdinal: ordinal))
                .ToArray();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new(GuestRetentionMutationStatus.Applied),
            new(GuestRetentionMutationStatus.Applied));
        FakeCandidateRepository repository = new(
            new(candidates, ReachedEnd: true));
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            repository,
            new()
            {
                ScanSize = 4,
                MutationBatchSize = 2
            },
            policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        CompleteGuestRetentionExecutionCommand completed =
            Assert.IsType<CompleteGuestRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(2, completed.ScannedCount);
        Assert.Equal(2, completed.NextAfterProjectionOrdinal);
        Assert.Equal(1, completed.RemainingCount);
        Assert.Equal(
            GuestRetentionCoordinates.BacklogOutcome,
            completed.OutcomeCode);
        Assert.Equal(1, completed.Attempt);
        Assert.All(
            dispatcher.ApplyAttempts,
            attempt => Assert.Equal(1, attempt));
        Assert.Equal(2, dispatcher.ApplyCount);
        Assert.Equal(2, result.AffectedCount);
    }

    [Fact]
    public async Task Retry_counts_prior_mutations_as_scanned_work()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot currentStay =
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.Confirmed,
                new DateOnly(2025, 1, 1),
                projectionOrdinal: 20);
        FakeDispatcher dispatcher = new(initialAffectedCount: 2);
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            new(new([currentStay], ReachedEnd: true)),
            new(),
            policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(attempt: 2),
                CancellationToken.None);

        CompleteGuestRetentionExecutionCommand completed =
            Assert.IsType<CompleteGuestRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(3, completed.ScannedCount);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(0, completed.NextAfterProjectionOrdinal);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(result.AffectedCount <= result.ScannedCount);
    }

    [Fact]
    public async Task Completion_time_is_normalized_before_the_first_result()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot currentStay =
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.Confirmed,
                new DateOnly(2025, 1, 1));
        DateTimeOffset subMicrosecondClock = GuestRetentionTestData.Now
            .ToOffset(TimeSpan.FromHours(2))
            .AddTicks(7);
        FakeDispatcher dispatcher = new(initialAffectedCount: 0);
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            new(new([currentStay], ReachedEnd: true)),
            new(),
            policy,
            subMicrosecondClock);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        CompleteGuestRetentionExecutionCommand completed =
            Assert.IsType<CompleteGuestRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(GuestRetentionTestData.Now, completed.CompletedAtUtc);
        Assert.Equal(completed.CompletedAtUtc, result.CompletedAtUtc);
    }

    [Fact]
    public async Task Projection_failure_has_a_stable_distinct_outcome()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        GuestRetentionStaySnapshot stay = Assert.Single(source.Stays);
        GuestRetentionCandidateSnapshot malformed = source with
        {
            Stays =
            [
                stay with { ProjectionSupported = false }
            ]
        };
        FakeDispatcher dispatcher = new(initialAffectedCount: 0);
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            new(new([malformed], ReachedEnd: true)),
            new(),
            policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Failed, result.Status);
        Assert.Equal(
            GuestRetentionCoordinates.ProjectionUnavailableOutcome,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.ApplyCount);
    }

    [Fact]
    public async Task Mutation_revalidation_policy_failure_is_reported()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot candidate =
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.Failed,
                Failure:
                    GuestRetentionMutationFailure.PolicyUnavailable));
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            new(new([candidate], ReachedEnd: true)),
            new(),
            policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Failed, result.Status);
        Assert.Equal(
            GuestRetentionCoordinates.PolicyUnavailableOutcome,
            result.OutcomeCode);
    }

    [Fact]
    public async Task Time_zone_failure_has_a_stable_distinct_outcome()
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property =
            Assert.Single(source.Properties);
        GuestRetentionCandidateSnapshot candidate = source with
        {
            Properties =
            [
                property with
                {
                    TimeZoneId = "UTC",
                    CanonicalTimeZoneId = "Etc/UTC",
                    TimeZoneStatus =
                        PropertyTimeZoneStatus.Alias,
                    TimeZoneCatalogVersion = null
                }
            ]
        };
        FakeDispatcher dispatcher = new(initialAffectedCount: 0);
        GuestRetentionContributor contributor = CreateContributor(
            dispatcher,
            new(new([candidate], ReachedEnd: true)),
            new(),
            policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Failed, result.Status);
        Assert.Equal(
            GuestRetentionCoordinates.TimeZoneUnavailableOutcome,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.ApplyCount);
    }

    private static GuestRetentionContributor CreateContributor(
        FakeDispatcher dispatcher,
        FakeCandidateRepository repository,
        GuestRetentionOptions options,
        GuestRetentionPolicyFixture policy,
        DateTimeOffset? clockUtcNow = null) =>
        new(
            dispatcher,
            repository,
            new GuestRetentionEligibilityEvaluator(policy.Registry),
            Options.Create(options),
            new FakeClock(clockUtcNow ?? GuestRetentionTestData.Now));

    private static RetentionContributionRequest Request(int attempt = 1) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            Guid.NewGuid(),
            TenantId,
            PropertyId: null,
            GuestRetentionCoordinates.OwnerKey,
            GuestRetentionCoordinates.DataClassKey,
            GuestRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            GuestRetentionTestData.Now.AddMinutes(-1),
            GuestRetentionTestData.Now.AddMinutes(10));

    private sealed class FakeClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeCandidateRepository(
        GuestRetentionScanPage page)
        : IGuestRetentionCandidateRepository
    {
        public Task<GuestRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(page);

        public Task<GuestRetentionCandidateSnapshot?> LoadAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDispatcher(
        int initialAffectedCount,
        params GuestRetentionMutationResult[] mutationResults)
        : IRequestDispatcher
    {
        private readonly Queue<GuestRetentionMutationResult> mutations =
            new(mutationResults);

        public int ApplyCount { get; private set; }
        public List<int> ApplyAttempts { get; } = [];
        public object? CompletedCommand { get; private set; }
        private int AppliedCount { get; set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            object response = command switch
            {
                BeginGuestRetentionExecutionCommand =>
                    new GuestRetentionExecutionStart(
                        DispatchRequired: true,
                        StartingProjectionOrdinal: 0,
                        AffectedCount: initialAffectedCount,
                        CompletedResult: null),
                ApplyGuestRetentionCommand apply =>
                    this.NextMutation(apply),
                CompleteGuestRetentionExecutionCommand completed =>
                    this.Complete(completed),
                _ => throw new NotSupportedException(
                    command.GetType().FullName)
            };
            return Task.FromResult(
                Result.Success((TResponse)response));
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private GuestRetentionMutationResult NextMutation(
            ApplyGuestRetentionCommand command)
        {
            this.ApplyCount++;
            this.ApplyAttempts.Add(command.Attempt);
            GuestRetentionMutationResult result =
                this.mutations.Dequeue();
            if (result.Status == GuestRetentionMutationStatus.Applied)
            {
                this.AppliedCount++;
            }

            return result;
        }

        private RetentionContributionResult Complete(
            CompleteGuestRetentionExecutionCommand command)
        {
            this.CompletedCommand = command;
            int affectedCount = checked(
                initialAffectedCount +
                this.AppliedCount);
            return new(
                RetentionExecutionContract.CurrentVersion,
                command.State switch
                {
                    GuestRetentionExecutionState.Completed =>
                        RetentionContributionStatus.Completed,
                    GuestRetentionExecutionState.Blocked =>
                        RetentionContributionStatus.Blocked,
                    GuestRetentionExecutionState.Failed =>
                        RetentionContributionStatus.Failed,
                    _ => throw new InvalidOperationException()
                },
                command.ScannedCount,
                affectedCount,
                command.RemainingCount,
                command.OutcomeCode,
                command.CompletedAtUtc,
                command.HoldReviewDueAtUtc);
        }
    }
}
