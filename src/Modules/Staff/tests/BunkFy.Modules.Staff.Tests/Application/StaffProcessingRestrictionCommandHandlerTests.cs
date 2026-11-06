namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffProcessingRestrictionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Apply_binds_tenant_scope_and_equivalent_retry_returns_receipt()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        RecordingRestrictionRepository restrictions = new();
        RecordingApprovalGate approvalGate = new();
        ApplyStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            approvalGate,
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());
        ApplyStaffProcessingRestrictionCommand command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            member.Id,
            member.Version,
            ExpectedProjectionRevision: 0,
            "user:privacy");

        Result<StaffProcessingRestrictionReceiptDto> applied =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<StaffProcessingRestrictionReceiptDto> replayed =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(applied.IsSuccess);
        Assert.Equal(applied.Value, replayed.Value);
        Assert.Equal(
            DataRightsCaseType.StaffRights,
            approvalGate.Request?.CaseType);
        Assert.Null(approvalGate.Request?.PropertyId);
        Assert.Equal(
            DataRightsOperation.Restriction,
            approvalGate.Request?.Operation);
        Assert.Equal(
            DataRightsRestrictionDirective.Apply,
            approvalGate.Request?.RestrictionDirective);
        Assert.Equal(member.Version, approvalGate.Request?.RecordVersion);
        Assert.True(projection.IsRestricted);
        Assert.Equal(1, projection.ActiveRestrictionCount);
        Assert.Equal(1, projection.Revision);
        Assert.Single(restrictions.Rows);
        Assert.Single(restrictions.Receipts);
    }

    [Fact]
    public async Task Apply_replays_receipt_committed_while_waiting_for_lock()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        ApplyStaffProcessingRestrictionCommand command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            member.Id,
            member.Version,
            ExpectedProjectionRevision: 0,
            "user:privacy");
        StaffProcessingRestrictionReceipt committed =
            StaffProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                member.ScopeId,
                command.IdempotencyKey,
                Guid.NewGuid(),
                StaffProcessingRestrictionAction.Apply,
                member.Id,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedStaffVersion,
                StaffProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                command.ActorId,
                Guid.NewGuid(),
                Now).Value;
        RecordingRestrictionRepository restrictions = new(
            receipts: [committed],
            receiptVisibleOnLookup: 2);
        ApplyStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            new RecordingApprovalGate(),
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());

        Result<StaffProcessingRestrictionReceiptDto> result =
            await handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(committed.Id, result.Value.ReceiptId);
        Assert.Equal(2, restrictions.ReceiptLookupCount);
        Assert.False(projection.IsRestricted);
        Assert.Equal(0, projection.Revision);
        Assert.Empty(restrictions.Rows);
    }

    [Fact]
    public async Task Changed_apply_retry_is_rejected_without_another_transition()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        RecordingRestrictionRepository restrictions = new();
        ApplyStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            new RecordingApprovalGate(),
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());
        ApplyStaffProcessingRestrictionCommand command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            member.Id,
            member.Version,
            ExpectedProjectionRevision: 0,
            "user:privacy");

        Assert.True(
            (await handler.HandleAsync(
                command,
                CancellationToken.None)).IsSuccess);
        Result<StaffProcessingRestrictionReceiptDto> conflict =
            await handler.HandleAsync(
                command with { ExpectedProjectionRevision = 1 },
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.RestrictionIdempotencyConflict,
            conflict.Error);
        Assert.Equal(1, projection.Revision);
        Assert.Single(restrictions.Receipts);
    }

    [Fact]
    public async Task Release_uses_release_directive_and_keeps_other_case_effective()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        StaffProcessingRestriction first = CreateRestriction(
            member,
            Guid.NewGuid(),
            approvalRevision: 2,
            Now.AddMinutes(-20));
        StaffProcessingRestriction second = CreateRestriction(
            member,
            Guid.NewGuid(),
            approvalRevision: 3,
            Now.AddMinutes(-15));
        Assert.True(
            projection.Apply(
                0,
                1,
                Now.AddMinutes(-20)).IsSuccess);
        Assert.True(
            projection.Apply(
                1,
                1,
                Now.AddMinutes(-15)).IsSuccess);
        RecordingRestrictionRepository restrictions =
            new([first, second]);
        RecordingApprovalGate approvalGate = new();
        ReleaseStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            approvalGate,
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());
        ReleaseStaffProcessingRestrictionCommand command = new(
            Guid.NewGuid(),
            first.Id,
            Guid.NewGuid(),
            8,
            member.Id,
            member.Version,
            first.Version,
            projection.Revision,
            "user:decision-maker");

        Result<StaffProcessingRestrictionReceiptDto> released =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(released.IsSuccess);
        Assert.Equal(
            DataRightsRestrictionDirective.Release,
            approvalGate.Request?.RestrictionDirective);
        Assert.Equal(
            StaffProcessingRestrictionState.Released,
            first.Status);
        Assert.Equal(
            StaffProcessingRestrictionState.Active,
            second.Status);
        Assert.True(projection.IsRestricted);
        Assert.Equal(1, projection.ActiveRestrictionCount);
        Assert.Equal(3, projection.Revision);
        Assert.True(released.Value.EffectiveRestricted);
        Assert.Equal(
            StaffProcessingRestrictionActionDto.Release,
            released.Value.Action);
    }

    [Fact]
    public async Task Release_replays_receipt_committed_while_waiting_for_lock()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        StaffProcessingRestriction restriction = CreateRestriction(
            member,
            Guid.NewGuid(),
            approvalRevision: 2,
            Now.AddMinutes(-20));
        Assert.True(
            projection.Apply(
                expectedRevision: 0,
                supportedContractVersion:
                    StaffProcessingRestrictionContract.CurrentVersion,
                occurredAtUtc: Now.AddMinutes(-20)).IsSuccess);
        ReleaseStaffProcessingRestrictionCommand command = new(
            Guid.NewGuid(),
            restriction.Id,
            Guid.NewGuid(),
            8,
            member.Id,
            member.Version,
            restriction.Version,
            projection.Revision,
            "user:decision-maker");
        StaffProcessingRestrictionReceipt committed =
            StaffProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                member.ScopeId,
                command.IdempotencyKey,
                restriction.Id,
                StaffProcessingRestrictionAction.Release,
                member.Id,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedStaffVersion,
                StaffProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion:
                    command.ExpectedRestrictionVersion + 1,
                resultingProjectionRevision:
                    command.ExpectedProjectionRevision + 1,
                effectiveRestricted: false,
                command.ActorId,
                Guid.NewGuid(),
                Now).Value;
        RecordingRestrictionRepository restrictions = new(
            rows: [restriction],
            receipts: [committed],
            receiptVisibleOnLookup: 2);
        ReleaseStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            new RecordingApprovalGate(),
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());

        Result<StaffProcessingRestrictionReceiptDto> result =
            await handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(committed.Id, result.Value.ReceiptId);
        Assert.Equal(2, restrictions.ReceiptLookupCount);
        Assert.Equal(
            StaffProcessingRestrictionState.Active,
            restriction.Status);
        Assert.True(projection.IsRestricted);
        Assert.Equal(1, projection.Revision);
    }

    [Fact]
    public async Task Denied_approval_does_not_change_projection_or_owner_state()
    {
        StaffMember member = CreateMember();
        StaffProcessingRestrictionProjection projection =
            CreateProjection(member);
        RecordingRestrictionRepository restrictions = new();
        ApplyStaffProcessingRestrictionCommandHandler handler = new(
            new RecordingMemberRepository(member),
            new RecordingProjectionRepository(projection),
            restrictions,
            new NoopStaffOperationLock(),
            new RecordingApprovalGate(isApproved: false),
            new TestScopeContext(),
            new TestClock(),
            new TestIdGenerator());

        Result<StaffProcessingRestrictionReceiptDto> result =
            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    member.Id,
                    member.Version,
                    0,
                    "user:privacy"),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.DataRightsApprovalRequired,
            result.Error);
        Assert.False(projection.IsRestricted);
        Assert.Empty(restrictions.Rows);
        Assert.Empty(restrictions.Receipts);
    }

    private static StaffMember CreateMember() => StaffMember.Create(
        Guid.NewGuid(),
        "tenant-a",
        "Staff member",
        null,
        null,
        null,
        null,
        null,
        null,
        "auth-subject",
        "user:creator",
        Guid.NewGuid(),
        Now.AddHours(-1)).Value;

    private static StaffProcessingRestrictionProjection CreateProjection(
        StaffMember member) =>
        StaffProcessingRestrictionProjection.Create(
            member.ScopeId,
            member.Id,
            StaffProcessingRestrictionContract.CurrentVersion,
            member.CreatedAtUtc).Value;

    private static StaffProcessingRestriction CreateRestriction(
        StaffMember member,
        Guid caseId,
        long approvalRevision,
        DateTimeOffset appliedAtUtc) =>
        StaffProcessingRestriction.Create(
            Guid.NewGuid(),
            member.ScopeId,
            member.Id,
            caseId,
            approvalRevision,
            member.Version,
            "user:privacy",
            appliedAtUtc).Value;

    private sealed class RecordingMemberRepository(StaffMember member)
        : IStaffMemberRepository
    {
        public Task AddAsync(
            StaffMember added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffMember?>(null);

        public Task<StaffMember?> GetForDataRightsAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(member.Id == staffMemberId ? member : null);

        public Task<StaffMember?> GetForSafetyTransitionAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
            string authSubjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetByAuthSubjectAsync(
            string authSubjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryMemberDto?> GetDirectoryAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(
            Guid propertyId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryListResponse> ListDirectoryAsync(
            string? search,
            StaffStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(
            Guid propertyId,
            string? search,
            StaffStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
            IReadOnlyCollection<string> authSubjectIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> EmployeeNumberExistsAsync(
            string employeeNumber,
            Guid? exceptStaffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AuthSubjectExistsAsync(
            string authSubjectId,
            Guid? exceptStaffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProjectionRepository(
        StaffProcessingRestrictionProjection projection)
        : IStaffProcessingRestrictionProjectionRepository
    {
        public Task<StaffProcessingRestrictionProjection?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                projection.StaffMemberId == staffMemberId
                    ? projection
                    : null);
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<StaffProcessingRestriction>? rows = null,
        IEnumerable<StaffProcessingRestrictionReceipt>? receipts = null,
        int receiptVisibleOnLookup = 1)
        : IStaffProcessingRestrictionRepository
    {
        public List<StaffProcessingRestriction> Rows { get; } =
            rows?.ToList() ?? [];
        public List<StaffProcessingRestrictionReceipt> Receipts { get; } =
            receipts?.ToList() ?? [];
        public int ReceiptLookupCount { get; private set; }

        public Task<StaffProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken)
        {
            this.ReceiptLookupCount++;
            return Task.FromResult(
                this.ReceiptLookupCount < receiptVisibleOnLookup
                    ? null
                    : this.Receipts.SingleOrDefault(receipt =>
                        receipt.IdempotencyKey == idempotencyKey));
        }

        public Task<StaffProcessingRestriction?> FindByApplyApprovalAsync(
            Guid staffMemberId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.StaffMemberId == staffMemberId &&
                restriction.ApplyCaseId == caseId &&
                restriction.ApplyApprovalRevision == approvalRevision));

        public Task<StaffProcessingRestriction?> FindByReleaseApprovalAsync(
            Guid staffMemberId,
            Guid caseId,
            long approvalRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.StaffMemberId == staffMemberId &&
                restriction.ReleaseCaseId == caseId &&
                restriction.ReleaseApprovalRevision == approvalRevision));

        public Task<StaffProcessingRestriction?> GetAsync(
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<StaffProcessingRestriction>>
            ListActiveAsync(
                Guid staffMemberId,
                PageRequest pageRequest,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<StaffProcessingRestriction>>(
                this.Rows
                    .Where(restriction =>
                        restriction.StaffMemberId == staffMemberId &&
                        restriction.Status ==
                            StaffProcessingRestrictionState.Active)
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());

        public Task AddAsync(
            StaffProcessingRestriction restriction,
            CancellationToken cancellationToken)
        {
            this.Rows.Add(restriction);
            return Task.CompletedTask;
        }

        public Task AddReceiptAsync(
            StaffProcessingRestrictionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Receipts.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApprovalGate(bool isApproved = true)
        : IDataRightsOperationApprovalGate
    {
        public DataRightsOperationApprovalRequest? Request { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(isApproved
                ? DataRightsOperationApprovalResult.Approved
                : DataRightsOperationApprovalResult.Denied(
                    DataRightsOperationApprovalDenial.CaseNotApproved));
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}
