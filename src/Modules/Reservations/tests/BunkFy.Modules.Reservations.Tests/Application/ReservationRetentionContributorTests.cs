namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRetentionContributorTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Mutation_cap_resumes_before_first_deferred_reservation()
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        ReservationRetentionCandidateSnapshot[] candidates =
            Enumerable.Range(1, 4)
                .Select(ordinal =>
                    ReservationRetentionTestData.Snapshot(
                        policy.Binding,
                        projectionOrdinal: ordinal))
                .ToArray();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new(ReservationRetentionMutationStatus.Applied),
            new(ReservationRetentionMutationStatus.Applied));
        ReservationRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new(candidates, ReachedEnd: true)),
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

        CompleteReservationRetentionExecutionCommand completed =
            Assert.IsType<
                CompleteReservationRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(2, completed.ScannedCount);
        Assert.Equal(2, completed.NextAfterProjectionOrdinal);
        Assert.Equal(1, completed.RemainingCount);
        Assert.Equal(
            ReservationRetentionCoordinates.BacklogOutcome,
            completed.OutcomeCode);
        Assert.Equal(2, dispatcher.ApplyCount);
        Assert.Equal(2, result.AffectedCount);
    }

    [Fact]
    public async Task Retry_counts_prior_mutations_as_scanned_work()
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        ReservationRetentionCandidateSnapshot current =
            ReservationRetentionTestData.Snapshot(
                policy.Binding,
                ReservationState.Confirmed,
                projectionOrdinal: 20);
        FakeDispatcher dispatcher = new(initialAffectedCount: 2);
        ReservationRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new([current], ReachedEnd: true)),
                new(),
                policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(attempt: 2),
                CancellationToken.None);

        CompleteReservationRetentionExecutionCommand completed =
            Assert.IsType<
                CompleteReservationRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(3, completed.ScannedCount);
        Assert.Equal(0, completed.NextAfterProjectionOrdinal);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(result.AffectedCount <= result.ScannedCount);
    }

    [Fact]
    public async Task Projection_failure_has_a_stable_distinct_outcome()
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        ReservationRetentionCandidateSnapshot malformed =
            ReservationRetentionTestData.Snapshot(policy.Binding) with
            {
                ProcessingRestrictionContractVersion = null
            };
        FakeDispatcher dispatcher = new(initialAffectedCount: 0);
        ReservationRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new([malformed], ReachedEnd: true)),
                new(),
                policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(
            RetentionContributionStatus.Failed,
            result.Status);
        Assert.Equal(
            ReservationRetentionCoordinates
                .ProjectionUnavailableOutcome,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.ApplyCount);
    }

    [Fact]
    public async Task Mutation_revalidation_policy_failure_is_reported()
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new ReservationRetentionMutationResult(
                ReservationRetentionMutationStatus.Failed,
                Failure:
                    ReservationRetentionMutationFailure
                        .PolicyUnavailable));
        ReservationRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new(
                    [ReservationRetentionTestData.Snapshot(policy.Binding)],
                    ReachedEnd: true)),
                new(),
                policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(
            RetentionContributionStatus.Failed,
            result.Status);
        Assert.Equal(
            ReservationRetentionCoordinates.PolicyUnavailableOutcome,
            result.OutcomeCode);
    }

    [Fact]
    public async Task Attempt_and_utc_microsecond_completion_are_exact()
    {
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new ReservationRetentionMutationResult(
                ReservationRetentionMutationStatus.Applied));
        DateTimeOffset clockValue =
            ReservationRetentionTestData.Now
                .ToOffset(TimeSpan.FromHours(2))
                .AddTicks(7);
        ReservationRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new(
                    [ReservationRetentionTestData.Snapshot(
                        policy.Binding)],
                    ReachedEnd: true)),
                new(),
                policy,
                clockValue);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(attempt: 2),
                CancellationToken.None);

        ApplyReservationRetentionCommand applied =
            Assert.IsType<ApplyReservationRetentionCommand>(
                dispatcher.AppliedCommand);
        CompleteReservationRetentionExecutionCommand completed =
            Assert.IsType<
                CompleteReservationRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(2, applied.Attempt);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(
            ReservationRetentionTestData.Now,
            completed.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, completed.CompletedAtUtc.Offset);
        Assert.Equal(completed.CompletedAtUtc, result.CompletedAtUtc);
    }

    private static ReservationRetentionContributor CreateContributor(
        FakeDispatcher dispatcher,
        FakeCandidateRepository repository,
        ReservationRetentionOptions options,
        ReservationRetentionPolicyFixture policy,
        DateTimeOffset? nowUtc = null) =>
        new(
            dispatcher,
            repository,
            new ReservationRetentionEligibilityEvaluator(
                policy.Registry),
            Options.Create(options),
            new FakeClock(
                nowUtc ?? ReservationRetentionTestData.Now));

    private static RetentionContributionRequest Request(
        int attempt = 1) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            Guid.NewGuid(),
            TenantId,
            PropertyId: null,
            ReservationRetentionCoordinates.OwnerKey,
            ReservationRetentionCoordinates.DataClassKey,
            ReservationRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            ReservationRetentionTestData.Now.AddMinutes(-1),
            ReservationRetentionTestData.Now.AddMinutes(10));

    private sealed class FakeClock(DateTimeOffset nowUtc)
        : ISystemClock
    {
        public DateTimeOffset UtcNow => nowUtc;
    }

    private sealed class FakeCandidateRepository(
        ReservationRetentionScanPage page)
        : IReservationRetentionCandidateRepository
    {
        public Task<ReservationRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(page);

        public Task<ReservationRetentionCandidateSnapshot?> LoadAsync(
            Guid propertyId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDispatcher(
        int initialAffectedCount,
        params ReservationRetentionMutationResult[] mutationResults)
        : IRequestDispatcher
    {
        private readonly Queue<ReservationRetentionMutationResult>
            mutations = new(mutationResults);

        public int ApplyCount { get; private set; }
        public object? AppliedCommand { get; private set; }
        public object? CompletedCommand { get; private set; }
        private int AppliedCount { get; set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            object response = command switch
            {
                BeginReservationRetentionExecutionCommand =>
                    new ReservationRetentionExecutionStart(
                        DispatchRequired: true,
                        StartingProjectionOrdinal: 0,
                        AffectedCount: initialAffectedCount,
                        CompletedResult: null),
                ApplyReservationRetentionCommand applied =>
                    this.NextMutation(applied),
                CompleteReservationRetentionExecutionCommand completed =>
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

        private ReservationRetentionMutationResult NextMutation(
            ApplyReservationRetentionCommand command)
        {
            this.ApplyCount++;
            this.AppliedCommand = command;
            ReservationRetentionMutationResult result =
                this.mutations.Dequeue();
            if (result.Status ==
                ReservationRetentionMutationStatus.Applied)
            {
                this.AppliedCount++;
            }

            return result;
        }

        private RetentionContributionResult Complete(
            CompleteReservationRetentionExecutionCommand command)
        {
            this.CompletedCommand = command;
            int affectedCount = checked(
                initialAffectedCount + this.AppliedCount);
            return new(
                RetentionExecutionContract.CurrentVersion,
                command.State switch
                {
                    ReservationRetentionExecutionState.Completed =>
                        RetentionContributionStatus.Completed,
                    ReservationRetentionExecutionState.Blocked =>
                        RetentionContributionStatus.Blocked,
                    ReservationRetentionExecutionState.Failed =>
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
