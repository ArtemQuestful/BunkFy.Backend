namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Policies;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyStaffRetentionCommandHandlerTests
{
    [Fact]
    public async Task Due_staff_member_commits_scrub_and_both_proofs_once()
    {
        Fixture fixture = CreateFixture(
            new RecordingPrerequisite(
                StaffRetentionAnonymisationPrerequisiteResult.Completed()));
        ApplyStaffRetentionCommand command = new(
            fixture.Execution.Id,
            Attempt: 1,
            fixture.Member.Id,
            fixture.Member.Version);

        Result<StaffRetentionMutationResult> first =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);
        Result<StaffRetentionMutationResult> replay =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionMutationStatus.Applied,
            first.Value.Status);
        Assert.Equal(
            StaffRetentionMutationStatus.AlreadyApplied,
            replay.Value.Status);
        Assert.Equal(StaffMemberState.Anonymised, fixture.Member.Status);
        Assert.Equal(
            StaffMember.AnonymisedDisplayName,
            fixture.Member.DisplayName);
        Assert.Null(fixture.Member.LegalName);
        Assert.Null(fixture.Member.WorkEmail);
        Assert.Null(fixture.Member.WorkPhone);
        Assert.Null(fixture.Member.EmployeeNumber);
        Assert.Null(fixture.Member.JobTitle);
        Assert.Null(fixture.Member.Department);
        Assert.Null(fixture.Member.AuthSubjectId);
        Assert.Equal(1, fixture.Execution.AffectedCount);
        Assert.Equal(1, fixture.Executions.AddProofCount);
        Assert.Equal(1, fixture.MemberMutationOperations.DeleteCount);
        Assert.Equal(1, fixture.Candidates.LoadCount);
        Assert.Equal(1, fixture.Lock.AcquireCount);
        Assert.Equal(0, fixture.Prerequisite?.PrepareCount);
        Assert.Equal(1, fixture.Prerequisite?.VerifyCount);
        Assert.NotNull(fixture.Executions.Receipt);
        Assert.NotNull(fixture.Executions.Tombstone);
        Assert.True(fixture.Executions.Tombstone.MatchesRetention(
            fixture.Executions.Receipt));
    }

    [Fact]
    public async Task Blocked_prerequisite_leaves_member_and_proofs_untouched()
    {
        Fixture fixture = CreateFixture(
            new RecordingPrerequisite(
                StaffRetentionAnonymisationPrerequisiteResult.Blocked(
                    "owner-access-protected")));

        Result<StaffRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Member.Id,
                    fixture.Member.Version),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionMutationStatus.Failed,
            result.Value.Status);
        Assert.Equal(
            StaffRetentionMutationFailure.PrerequisiteBlocked,
            result.Value.Failure);
        Assert.Equal(StaffMemberState.Departed, fixture.Member.Status);
        Assert.Equal("Casey Morgan", fixture.Member.DisplayName);
        Assert.Equal(0, fixture.Execution.AffectedCount);
        Assert.Equal(0, fixture.Executions.AddProofCount);
        Assert.Equal(0, fixture.Prerequisite?.PrepareCount);
        Assert.Equal(1, fixture.Prerequisite?.VerifyCount);
    }

    [Fact]
    public async Task Missing_prerequisite_fails_closed_before_mutation()
    {
        Fixture fixture = CreateFixture(prerequisite: null);

        Result<StaffRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Member.Id,
                    fixture.Member.Version),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionMutationStatus.Failed,
            result.Value.Status);
        Assert.Equal(
            StaffRetentionMutationFailure.PrerequisiteUnavailable,
            result.Value.Failure);
        Assert.Equal(StaffMemberState.Departed, fixture.Member.Status);
        Assert.Equal(0, fixture.Execution.AffectedCount);
        Assert.Equal(0, fixture.Executions.AddProofCount);
    }

    [Fact]
    public async Task Stale_attempt_is_rejected_before_discovery()
    {
        Fixture fixture = CreateFixture(
            new RecordingPrerequisite(
                StaffRetentionAnonymisationPrerequisiteResult.Completed()));

        Result<StaffRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 2,
                    fixture.Member.Id,
                    fixture.Member.Version),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.RetentionExecutionNotFound,
            result.Error);
        Assert.Equal(0, fixture.Candidates.LoadCount);
        Assert.Equal(0, fixture.Lock.AcquireCount);
        Assert.Equal(StaffMemberState.Departed, fixture.Member.Status);
    }

    [Fact]
    public async Task Unresolved_workspace_anchor_blocks_retention_anonymisation()
    {
        Fixture fixture = CreateFixture(
            new RecordingPrerequisite(
                StaffRetentionAnonymisationPrerequisiteResult.Completed()),
            hasUnresolvedAnchor: true);

        Result<StaffRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Member.Id,
                    fixture.Member.Version),
                CancellationToken.None);

        Assert.Equal(
            StaffRetentionMutationStatus.Failed,
            result.Value.Status);
        Assert.Equal(
            StaffRetentionMutationFailure.IdentityAnchorResolutionRequired,
            result.Value.Failure);
        Assert.Equal(StaffMemberState.Departed, fixture.Member.Status);
        Assert.Equal(0, fixture.Executions.AddProofCount);
        Assert.Equal(0, fixture.MemberMutationOperations.DeleteCount);
        Assert.Equal(1, fixture.Lock.AcquireCount);
    }

    private static Fixture CreateFixture(
        RecordingPrerequisite? prerequisite,
        bool hasUnresolvedAnchor = false)
    {
        StaffRetentionPolicyFixture policy =
            StaffRetentionTestData.CreatePolicy();
        DateTimeOffset departedAtUtc =
            StaffRetentionTestData.Now.AddDays(-400);
        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Casey Morgan",
            "Casey A. Morgan",
            "casey@example.test",
            "+44 20 1234 5678",
            "EMP-17",
            "General manager",
            "Operations",
            "subject:artem",
            "user:creator",
            Guid.NewGuid(),
            departedAtUtc.AddDays(-100)).Value;
        Result departed = member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:manager",
            "Employment ended",
            Guid.NewGuid(),
            [],
            departedAtUtc);
        Assert.True(departed.IsSuccess);
        member.ClearDomainEvents();

        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                Guid.NewGuid(),
                member.ScopeId,
                StaffRetentionCoordinates.DataClassKey,
                StaffRetentionCoordinates.ExecutionPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                StaffRetentionTestData.Now.AddMinutes(-1),
                StaffRetentionTestData.Now.AddMinutes(15)).Value;
        RecordingExecutionRepository executions = new(execution);
        RecordingCandidateRepository candidates = new(
            StaffRetentionTestData.Snapshot(
                policy.Governance,
                staffVersion: member.Version,
                staffMemberId: member.Id,
                departedAtUtc: departedAtUtc));
        RecordingOperationLock operationLock = new(initialRevision: 8);
        RecordingMemberMutationOperations memberMutationOperations = new();
        StaffRetentionPrerequisiteEvaluator prerequisites = new(
            prerequisite is null
                ? []
                : [prerequisite]);
        ApplyStaffRetentionCommandHandler handler = new(
            executions,
            candidates,
            new StubStaffMemberRepository(member),
            operationLock,
            memberMutationOperations,
            new StubStaffIdentityProvisioningAnchorResolutionRepository(
                hasUnresolvedAnchor),
            new StaffRetentionEligibilityEvaluator(policy.Registry),
            prerequisites,
            new TestScopeContext(member.ScopeId),
            new TestClock(),
            new QueueIdGenerator(
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new(
            handler,
            executions,
            candidates,
            operationLock,
            memberMutationOperations,
            prerequisite,
            execution,
            member);
    }

    private sealed record Fixture(
        ApplyStaffRetentionCommandHandler Handler,
        RecordingExecutionRepository Executions,
        RecordingCandidateRepository Candidates,
        RecordingOperationLock Lock,
        RecordingMemberMutationOperations MemberMutationOperations,
        RecordingPrerequisite? Prerequisite,
        StaffRetentionExecution Execution,
        StaffMember Member);

    private sealed class RecordingMemberMutationOperations
        : IStaffMemberMutationOperationRepository
    {
        public int DeleteCount { get; private set; }

        public Task<StaffMemberMutationOperationRecord?> GetAsync(
            Guid staffMemberId,
            Guid operationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            StaffMemberMutationOperationRecord operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteForStaffMemberAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionRepository(
        StaffRetentionExecution execution)
        : IStaffRetentionExecutionRepository
    {
        public StaffRetentionAnonymisationReceipt? Receipt
        {
            get;
            private set;
        }

        public StaffAnonymisationTombstone? Tombstone
        {
            get;
            private set;
        }

        public int AddProofCount { get; private set; }

        public Task<StaffRetentionExecution?> GetExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                execution.Id == executionId ? execution : null);

        public Task AddExecutionAsync(
            StaffRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffRetentionSweepCheckpoint?> GetCheckpointAsync(
            string dataClassKey,
            int executionPolicyVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCheckpointAsync(
            StaffRetentionSweepCheckpoint checkpoint,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffRetentionAnonymisationReceipt?> GetReceiptAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Receipt?.StaffMemberId == staffMemberId
                    ? this.Receipt
                    : null);

        public Task<StaffAnonymisationTombstone?> GetTombstoneAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Tombstone?.Id == staffMemberId
                    ? this.Tombstone
                    : null);

        public Task AddAnonymisationProofAsync(
            StaffRetentionAnonymisationReceipt receipt,
            StaffAnonymisationTombstone tombstone,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            this.Tombstone = tombstone;
            this.AddProofCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCandidateRepository(
        StaffRetentionCandidateSnapshot snapshot)
        : IStaffRetentionCandidateRepository
    {
        public int LoadCount { get; private set; }

        public Task<StaffRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffRetentionCandidateSnapshot?> LoadAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.LoadCount++;
            return Task.FromResult(
                snapshot.StaffMemberId == staffMemberId
                    ? snapshot
                    : null);
        }
    }

    private sealed class RecordingOperationLock(long initialRevision)
        : IStaffOperationLock
    {
        private long revision = initialRevision;

        public int AcquireCount { get; private set; }

        public Task<long?> GetStaffMemberRevisionAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<long?>(this.revision);

        public Task<bool> TryAcquireStaffMemberAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.AcquireCount++;
            this.revision++;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingPrerequisite(
        StaffRetentionAnonymisationPrerequisiteResult result)
        : IStaffRetentionAnonymisationPrerequisite
    {
        public string ContributorKey => "workspace-access";

        public int PrepareCount { get; private set; }

        public int VerifyCount { get; private set; }

        public Task<StaffRetentionAnonymisationPrerequisiteResult>
            PrepareAsync(
                StaffRetentionAnonymisationPrerequisiteRequest request,
                CancellationToken cancellationToken)
        {
            this.PrepareCount++;
            return Task.FromResult(result);
        }

        public Task<StaffRetentionAnonymisationPrerequisiteResult>
            VerifyAsync(
                StaffRetentionAnonymisationPrerequisiteRequest request,
                CancellationToken cancellationToken)
        {
            this.VerifyCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => StaffRetentionTestData.Now;
    }

    private sealed class QueueIdGenerator(params Guid[] values)
        : IIdGenerator
    {
        private readonly Queue<Guid> values = new(values);

        public Guid NewId() => this.values.Dequeue();
    }
}
