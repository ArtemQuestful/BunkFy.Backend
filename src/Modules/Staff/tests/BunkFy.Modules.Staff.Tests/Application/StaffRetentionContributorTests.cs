namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Policies;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffRetentionContributorTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Mutation_cap_resumes_before_first_deferred_member()
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        StaffRetentionCandidateSnapshot[] candidates =
            Enumerable.Range(1, 4)
                .Select(ordinal =>
                    StaffRetentionTestData.Snapshot(
                        policy.Governance,
                        projectionOrdinal: ordinal))
                .ToArray();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new(StaffRetentionMutationStatus.Applied),
            new(StaffRetentionMutationStatus.Applied));
        StaffRetentionContributor contributor =
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

        CompleteStaffRetentionExecutionCommand completed =
            Assert.IsType<CompleteStaffRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(2, completed.ScannedCount);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(2, completed.NextAfterProjectionOrdinal);
        Assert.Equal(1, completed.RemainingCount);
        Assert.Equal(
            StaffRetentionCoordinates.BacklogOutcome,
            completed.OutcomeCode);
        Assert.Equal(2, dispatcher.ApplyCount);
        Assert.Equal(2, result.AffectedCount);
    }

    [Fact]
    public async Task Retry_counts_prior_mutations_as_scanned_work()
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        FakeDispatcher dispatcher = new(initialAffectedCount: 2);
        StaffRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new(
                    [
                        StaffRetentionTestData.Snapshot(
                            policy.Governance,
                            departedAtUtc:
                                StaffRetentionTestData.Now.AddDays(-10),
                            projectionOrdinal: 20)
                    ],
                    ReachedEnd: true)),
                new(),
                policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(attempt: 2),
                CancellationToken.None);

        CompleteStaffRetentionExecutionCommand completed =
            Assert.IsType<CompleteStaffRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(3, completed.ScannedCount);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(0, completed.NextAfterProjectionOrdinal);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(result.AffectedCount <= result.ScannedCount);
    }

    [Fact]
    public async Task Held_profile_does_not_starve_later_due_profile()
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        DateTimeOffset holdPlacedAtUtc =
            StaffRetentionTestData.Now.AddDays(-2);
        StaffRetentionCandidateSnapshot held =
            StaffRetentionTestData.Snapshot(
                policy.Governance,
                projectionOrdinal: 1) with
            {
                ActiveHoldCount = 1,
                EarliestHoldPlacedAtUtc = holdPlacedAtUtc
            };
        StaffRetentionCandidateSnapshot due =
            StaffRetentionTestData.Snapshot(
                policy.Governance,
                projectionOrdinal: 2);
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0,
            new StaffRetentionMutationResult(
                StaffRetentionMutationStatus.Applied));
        StaffRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new([held, due], ReachedEnd: true)),
                new(),
                policy);

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        CompleteStaffRetentionExecutionCommand completed =
            Assert.IsType<CompleteStaffRetentionExecutionCommand>(
                dispatcher.CompletedCommand);
        Assert.Equal(RetentionContributionStatus.Blocked, result.Status);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(1, dispatcher.ApplyCount);
        Assert.Equal(0, completed.NextAfterProjectionOrdinal);
        Assert.Equal(holdPlacedAtUtc, result.HoldReviewDueAtUtc);
    }

    [Fact]
    public async Task Projection_failure_has_a_stable_distinct_outcome()
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        StaffRetentionCandidateSnapshot malformed =
            StaffRetentionTestData.Snapshot(policy.Governance) with
            {
                ProcessingRestrictionContractVersion = null
            };
        FakeDispatcher dispatcher = new(initialAffectedCount: 0);
        StaffRetentionContributor contributor =
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
            StaffRetentionCoordinates.ProjectionUnavailableOutcome,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.ApplyCount);
    }

    [Fact]
    public async Task Prerequisite_failure_has_a_stable_distinct_outcome()
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        FakeDispatcher dispatcher = new(
            initialAffectedCount: 0);
        StaffRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new(new(
                    [StaffRetentionTestData.Snapshot(policy.Governance)],
                    ReachedEnd: true)),
                new(),
                policy,
                StaffRetentionAnonymisationPrerequisiteResult
                    .RetryRequired("workspace-access-unavailable"));

        RetentionContributionResult result =
            await contributor.ExecuteAsync(
                Request(),
                CancellationToken.None);

        Assert.Equal(
            RetentionContributionStatus.Failed,
            result.Status);
        Assert.Equal(
            StaffRetentionCoordinates.PrerequisiteUnavailableOutcome,
            result.OutcomeCode);
        Assert.Equal(0, dispatcher.ApplyCount);
    }

    private static StaffRetentionContributor CreateContributor(
        FakeDispatcher dispatcher,
        FakeCandidateRepository repository,
        StaffRetentionOptions options,
        StaffRetentionPolicyFixture policy,
        StaffRetentionAnonymisationPrerequisiteResult?
            prerequisiteResult = null) =>
        new(
            dispatcher,
            repository,
            new StaffRetentionEligibilityEvaluator(policy.Registry),
            new StaffRetentionPrerequisiteEvaluator(
                [
                    new StubPrerequisite(
                        prerequisiteResult ??
                        StaffRetentionAnonymisationPrerequisiteResult
                            .Completed())
                ]),
            Options.Create(options),
            new FakeClock());

    private static RetentionContributionRequest Request(
        int attempt = 1) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            Guid.NewGuid(),
            TenantId,
            PropertyId: null,
            StaffRetentionCoordinates.OwnerKey,
            StaffRetentionCoordinates.DataClassKey,
            StaffRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            StaffRetentionTestData.Now.AddMinutes(-1),
            StaffRetentionTestData.Now.AddMinutes(10));

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => StaffRetentionTestData.Now;
    }

    private sealed class StubPrerequisite(
        StaffRetentionAnonymisationPrerequisiteResult result)
        : IStaffRetentionAnonymisationPrerequisite
    {
        public string ContributorKey => "workspace-access";

        public Task<StaffRetentionAnonymisationPrerequisiteResult>
            PrepareAsync(
                StaffRetentionAnonymisationPrerequisiteRequest request,
                CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<StaffRetentionAnonymisationPrerequisiteResult>
            VerifyAsync(
                StaffRetentionAnonymisationPrerequisiteRequest request,
                CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FakeCandidateRepository(
        StaffRetentionScanPage page)
        : IStaffRetentionCandidateRepository
    {
        public Task<StaffRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(page);

        public Task<StaffRetentionCandidateSnapshot?> LoadAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDispatcher(
        int initialAffectedCount,
        params StaffRetentionMutationResult[] mutationResults)
        : IRequestDispatcher
    {
        private readonly Queue<StaffRetentionMutationResult>
            mutations = new(mutationResults);

        public int ApplyCount { get; private set; }
        public object? CompletedCommand { get; private set; }
        private int AppliedCount { get; set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            object response = command switch
            {
                BeginStaffRetentionExecutionCommand =>
                    new StaffRetentionExecutionStart(
                        DispatchRequired: true,
                        StartingProjectionOrdinal: 0,
                        AffectedCount: initialAffectedCount,
                        CompletedResult: null),
                ApplyStaffRetentionCommand => this.NextMutation(),
                CompleteStaffRetentionExecutionCommand completed =>
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

        private StaffRetentionMutationResult NextMutation()
        {
            this.ApplyCount++;
            StaffRetentionMutationResult result =
                this.mutations.Dequeue();
            if (result.Status ==
                StaffRetentionMutationStatus.Applied)
            {
                this.AppliedCount++;
            }

            return result;
        }

        private RetentionContributionResult Complete(
            CompleteStaffRetentionExecutionCommand command)
        {
            this.CompletedCommand = command;
            int affectedCount = checked(
                initialAffectedCount + this.AppliedCount);
            return new(
                RetentionExecutionContract.CurrentVersion,
                command.State switch
                {
                    StaffRetentionExecutionState.Completed =>
                        RetentionContributionStatus.Completed,
                    StaffRetentionExecutionState.Blocked =>
                        RetentionContributionStatus.Blocked,
                    StaffRetentionExecutionState.Failed =>
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
