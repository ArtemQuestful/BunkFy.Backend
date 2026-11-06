namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyStaffAnonymisationCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private const long SelectedLockRevision = 42;
    private const long ResultingLockRevision = 43;

    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_approval_commits_scrubbed_member_receipt_and_tombstone()
    {
        Scenario scenario = CreateScenario();
        Guid eventId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        ApplyStaffAnonymisationCommandHandler handler =
            scenario.CreateHandler(
                new QueueIdGenerator(eventId, receiptId));

        Result<StaffAnonymisationReceiptDto> result =
            await handler.HandleAsync(
                scenario.Command,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(
            scenario.Member.MatchesAnonymisedState(
                version: 3,
                Now));
        Assert.Equal(StaffMemberState.Anonymised, scenario.Member.Status);
        Assert.Equal("Anonymised staff member", scenario.Member.DisplayName);
        Assert.Null(scenario.Member.LegalName);
        Assert.Null(scenario.Member.WorkEmail);
        Assert.Null(scenario.Member.WorkPhone);
        Assert.Null(scenario.Member.EmployeeNumber);
        Assert.Null(scenario.Member.JobTitle);
        Assert.Null(scenario.Member.Department);
        Assert.Null(scenario.Member.AuthSubjectId);
        Assert.Equal(eventId, result.Value.EventId);
        Assert.Equal(receiptId, result.Value.ReceiptId);
        Assert.Equal(SelectedLockRevision,
            result.Value.SelectedOperationLockRevision);
        Assert.Equal(ResultingLockRevision,
            result.Value.ResultingOperationLockRevision);
        Assert.Equal(64, result.Value.ApprovalEvidenceSha256.Length);
        Assert.Equal(64, result.Value.StateBindingsSha256.Length);
        Assert.Equal(64, result.Value.CanonicalSha256.Length);
        Assert.NotNull(scenario.Anonymisation.Receipt);
        Assert.NotNull(scenario.Anonymisation.Tombstone);
        Assert.True(
            scenario.Anonymisation.Tombstone.Matches(
                scenario.Anonymisation.Receipt));
        Assert.Equal(1, scenario.Anonymisation.AddCount);
        Assert.Equal(1, scenario.MemberMutationOperations.DeleteCount);
        Assert.Equal(1, scenario.OperationLock.AcquireCount);
        Assert.Equal(1, scenario.ApprovalGate.CallCount);
    }

    [Fact]
    public async Task Equivalent_retry_returns_identical_proof_without_revalidating()
    {
        Scenario scenario = CreateScenario();
        ApplyStaffAnonymisationCommandHandler handler =
            scenario.CreateHandler(
                new QueueIdGenerator(
                    Guid.NewGuid(),
                    Guid.NewGuid()));

        Result<StaffAnonymisationReceiptDto> first =
            await handler.HandleAsync(
                scenario.Command,
                CancellationToken.None);
        Result<StaffAnonymisationReceiptDto> replay =
            await handler.HandleAsync(
                scenario.Command,
                CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(3, scenario.Member.Version);
        Assert.Equal(1, scenario.Anonymisation.AddCount);
        Assert.Equal(1, scenario.MemberMutationOperations.DeleteCount);
        Assert.Equal(1, scenario.OperationLock.AcquireCount);
        Assert.Equal(1, scenario.ApprovalGate.CallCount);
    }

    [Fact]
    public async Task Reused_key_with_changed_operation_or_evidence_fails_closed()
    {
        Scenario scenario = CreateScenario();
        ApplyStaffAnonymisationCommandHandler handler =
            scenario.CreateHandler(
                new QueueIdGenerator(
                    Guid.NewGuid(),
                    Guid.NewGuid()));
        Assert.True((await handler.HandleAsync(
            scenario.Command,
            CancellationToken.None)).IsSuccess);

        Result<StaffAnonymisationReceiptDto> changedRevision =
            await handler.HandleAsync(
                scenario.Command with
                {
                    OperationRevision =
                        scenario.Command.OperationRevision + 1
                },
                CancellationToken.None);
        Result<StaffAnonymisationReceiptDto> changedEvidence =
            await handler.HandleAsync(
                scenario.Command with
                {
                    ApprovalEvidence =
                        scenario.Command.ApprovalEvidence with
                        {
                            ContentSha256 = new string('b', 64)
                        }
                },
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.AnonymisationIdempotencyConflict,
            changedRevision.Error);
        Assert.Equal(
            StaffApplicationErrors.AnonymisationIdempotencyConflict,
            changedEvidence.Error);
        Assert.Equal(3, scenario.Member.Version);
        Assert.Equal(1, scenario.Anonymisation.AddCount);
    }

    [Fact]
    public async Task Changed_frozen_state_is_rejected_before_owner_mutation()
    {
        Scenario scenario = CreateScenario(
            evidenceLockRevision: SelectedLockRevision - 1);
        ApplyStaffAnonymisationCommandHandler handler =
            scenario.CreateHandler(new QueueIdGenerator());

        Result<StaffAnonymisationReceiptDto> result =
            await handler.HandleAsync(
                scenario.Command,
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.AnonymisationStateChanged,
            result.Error);
        Assert.Equal(StaffMemberState.Departed, scenario.Member.Status);
        Assert.Equal(2, scenario.Member.Version);
        Assert.Null(scenario.Anonymisation.Receipt);
        Assert.Equal(1, scenario.OperationLock.AcquireCount);
        Assert.Equal(1, scenario.ApprovalGate.CallCount);
    }

    [Fact]
    public async Task Unresolved_workspace_anchor_blocks_anonymisation()
    {
        Scenario scenario = CreateScenario();
        ApplyStaffAnonymisationCommandHandler handler =
            scenario.CreateHandler(
                new QueueIdGenerator(),
                new StubStaffIdentityProvisioningAnchorResolutionRepository(
                    hasUnresolved: true));

        Result<StaffAnonymisationReceiptDto> result =
            await handler.HandleAsync(
                scenario.Command,
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorResolutionRequired,
            result.Error);
        Assert.Equal(StaffMemberState.Departed, scenario.Member.Status);
        Assert.Null(scenario.Anonymisation.Receipt);
        Assert.Equal(0, scenario.MemberMutationOperations.DeleteCount);
        Assert.Equal(1, scenario.OperationLock.AcquireCount);
    }

    private static Scenario CreateScenario(
        long evidenceLockRevision = SelectedLockRevision)
    {
        DateTimeOffset departedAtUtc = Now.AddDays(-2_556);
        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            TenantId,
            "Private Staff Name",
            legalName: "Private Legal Name",
            workEmail: "private.staff@example.test",
            workPhone: "+44 20 1234 5678",
            employeeNumber: "private-employee-number",
            jobTitle: "Private job title",
            department: "Private department",
            authSubjectId: "private-auth-subject",
            "user:creator",
            Guid.NewGuid(),
            departedAtUtc.AddDays(-30)).Value;
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:manager",
            "employment-ended",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        member.ClearDomainEvents();

        StaffEmploymentGovernance governance =
            StaffEmploymentGovernance.Configure(
                TenantId,
                member.Id,
                member.Version,
                CreateGovernanceBinding(),
                [
                    StaffEmploymentGovernanceAcknowledgement.Create(
                        "operator-notice",
                        1).Value
                ],
                "user:privacy",
                Now.AddMinutes(-2)).Value;
        StaffProcessingRestrictionProjection restriction =
            StaffProcessingRestrictionProjection.Create(
                TenantId,
                member.Id,
                StaffProcessingRestrictionContract.CurrentVersion,
                Now.AddMinutes(-2)).Value;
        IReadOnlyCollection<StaffDataHold> holds = [];
        DataRightsApprovalEvidence evidence = CreateApprovalEvidence(
            member,
            governance,
            restriction,
            holds,
            evidenceLockRevision);
        ApplyStaffAnonymisationCommand command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalRevision: 4,
            OperationRevision: 5,
            member.Id,
            member.Version,
            evidence,
            "user:privacy-executor");
        RecordingAnonymisationRepository anonymisation = new();
        RecordingMemberMutationOperations memberMutationOperations = new();
        RecordingOperationLock operationLock = new();
        RecordingApprovalGate approvalGate = new(evidence);
        return new(
            member,
            governance,
            restriction,
            holds,
            command,
            anonymisation,
            memberMutationOperations,
            operationLock,
            approvalGate);
    }

    private static StaffEmploymentGovernanceBinding
        CreateGovernanceBinding() =>
        StaffEmploymentGovernanceBinding.Create(
            "GB",
            "staff-test",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "staff-employment",
            1,
            new string('a', 64),
            new DateTimeOffset(
                2026,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                2027,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            Now.AddMinutes(-3)).Value;

    private static DataRightsApprovalEvidence CreateApprovalEvidence(
        StaffMember member,
        StaffEmploymentGovernance governance,
        StaffProcessingRestrictionProjection restriction,
        IReadOnlyCollection<StaffDataHold> holds,
        long operationLockRevision) =>
        new(
            SchemaVersion: 2,
            PropertyId: null,
            PropertyVersion: 0,
            OperatingCountryCode: "GB",
            PolicyId: "staff-test",
            PolicyVersion: 1,
            RetentionPolicyId: "staff-employment",
            RetentionPolicyVersion: 1,
            ContentSha256: new string('a', 64),
            PurposeCode: "staff-data-rights-anonymisation",
            Surface: "erasure",
            SourceProvenance: "authorized-workspace-operator",
            EvaluatedAtUtc: Now.AddMinutes(-1),
            RequiresDistinctExecutor: true,
            CaseType: DataRightsCaseType.StaffRights,
            ScopeKind: DataRightsExecutionScopeKind.Tenant,
            RetentionDataClass: "staff-employment",
            RetentionTrigger: "employment-ended",
            RetentionTriggeredAtUtc: member.DepartedAtUtc,
            RetentionDeadlineUtc: Now.AddDays(-1),
            StateBindings:
                StaffAnonymisationPolicyEvidence.CreateBindings(
                    member,
                    governance,
                    restriction,
                    holds,
                    operationLockRevision),
            StateBindingsSha256: new string('c', 64));

    private sealed record Scenario(
        StaffMember Member,
        StaffEmploymentGovernance Governance,
        StaffProcessingRestrictionProjection Restriction,
        IReadOnlyCollection<StaffDataHold> Holds,
        ApplyStaffAnonymisationCommand Command,
        RecordingAnonymisationRepository Anonymisation,
        RecordingMemberMutationOperations MemberMutationOperations,
        RecordingOperationLock OperationLock,
        RecordingApprovalGate ApprovalGate)
    {
        public ApplyStaffAnonymisationCommandHandler CreateHandler(
            IIdGenerator ids,
            IStaffIdentityProvisioningAnchorResolutionRepository?
                resolutions = null) =>
            new(
                new StubStaffMemberRepository(this.Member),
                new StubGovernanceRepository(this.Governance),
                new StubRestrictionRepository(this.Restriction),
                new StubHoldRepository(this.Holds),
                this.OperationLock,
                this.Anonymisation,
                this.MemberMutationOperations,
                resolutions ??
                new StubStaffIdentityProvisioningAnchorResolutionRepository(),
                this.ApprovalGate,
                new TestScopeContext(),
                new TestClock(),
                ids);
    }

    private sealed class StubGovernanceRepository(
        StaffEmploymentGovernance governance)
        : IStaffEmploymentGovernanceRepository
    {
        public Task<StaffEmploymentGovernance?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffEmploymentGovernance?>(
                governance.StaffMemberId == staffMemberId
                    ? governance
                    : null);

        public Task<StaffEmploymentGovernanceChangeReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            StaffEmploymentGovernance added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            StaffEmploymentGovernanceChangeReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubRestrictionRepository(
        StaffProcessingRestrictionProjection restriction)
        : IStaffProcessingRestrictionProjectionRepository
    {
        public Task<StaffProcessingRestrictionProjection?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffProcessingRestrictionProjection?>(
                restriction.StaffMemberId == staffMemberId
                    ? restriction
                    : null);
    }

    private sealed class StubHoldRepository(
        IReadOnlyCollection<StaffDataHold> holds)
        : IStaffDataHoldRepository
    {
        public Task<StaffDataHoldReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDataHold?> GetAsync(
            Guid staffMemberId,
            Guid holdId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<StaffDataHold>> ListAsync(
            Guid staffMemberId,
            StaffDataHoldStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<StaffDataHold>>(
                holds.Where(hold =>
                        hold.StaffMemberId == staffMemberId &&
                        (status is null ||
                         (int)hold.State == (int)status.Value))
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());

        public Task<long> CountAsync(
            Guid staffMemberId,
            StaffDataHoldStatus? status,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                holds.LongCount(hold =>
                    hold.StaffMemberId == staffMemberId &&
                    (status is null ||
                     (int)hold.State == (int)status.Value)));

        public Task AddAsync(
            StaffDataHold hold,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddReceiptAsync(
            StaffDataHoldReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOperationLock
        : IStaffOperationLock
    {
        public int AcquireCount { get; private set; }

        public Task<long?> GetStaffMemberRevisionAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<long?>(ResultingLockRevision);

        public Task<bool> TryAcquireStaffMemberAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.AcquireCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingAnonymisationRepository
        : IStaffAnonymisationRepository
    {
        public StaffAnonymisationReceipt? Receipt { get; private set; }
        public StaffAnonymisationTombstone? Tombstone { get; private set; }
        public int AddCount { get; private set; }

        public Task<StaffAnonymisationReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Receipt?.IdempotencyKey == idempotencyKey
                    ? this.Receipt
                    : null);

        public Task<StaffAnonymisationTombstone?> GetTombstoneAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Tombstone?.Id == staffMemberId
                    ? this.Tombstone
                    : null);

        public Task AddAsync(
            StaffAnonymisationReceipt receipt,
            StaffAnonymisationTombstone tombstone,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            this.Tombstone = tombstone;
            this.AddCount++;
            return Task.CompletedTask;
        }
    }

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

    private sealed class RecordingApprovalGate(
        DataRightsApprovalEvidence evidence)
        : IDataRightsOperationApprovalGate
    {
        public int CallCount { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            Assert.Equal(DataRightsCaseType.StaffRights, request.CaseType);
            Assert.Null(request.PropertyId);
            return Task.FromResult(
                DataRightsOperationApprovalResult
                    .ApprovedWithEvidence(evidence));
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class QueueIdGenerator(params Guid[] values)
        : IIdGenerator
    {
        private readonly Queue<Guid> ids = new(values);

        public Guid NewId() => this.ids.Dequeue();
    }
}
