namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyStaffDataRightsCorrectionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_correction_mutates_departed_profile_and_records_owner_proof()
    {
        StaffMember member = CreateMember();
        Assert.True(member.Depart(
            new DateOnly(2026, 7, 28),
            member.Version,
            "user:owner",
            "Contract ended",
            Guid.NewGuid(),
            [],
            Now.AddMinutes(-1)).IsSuccess);
        long selectedVersion = member.Version;
        RecordingExecutionGate gate = new();
        InMemoryReceiptRepository receipts = new();
        ApplyStaffDataRightsCorrectionCommandHandler handler = CreateHandler(
            member,
            receipts,
            gate);
        ApplyStaffDataRightsCorrectionCommand command = Command(
            member,
            selectedVersion) with
        {
            DisplayName = "Ada Lovelace",
            WorkEmail = "ADA.NEW@EXAMPLE.TEST"
        };

        Result<StaffDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(StaffMemberState.Departed, member.Status);
        Assert.Equal(selectedVersion + 1, member.Version);
        Assert.Equal("Ada Lovelace", member.DisplayName);
        Assert.Equal("ada.new@example.test", member.WorkEmail);
        Assert.Equal(
            [StaffDataRightsFieldKeys.DisplayName, StaffDataRightsFieldKeys.WorkEmail],
            result.Value.ChangedFieldKeys);
        Assert.NotNull(receipts.Receipt);
        Assert.Equal(command.ExecutionId, receipts.Receipt.ExecutionId);
        Assert.Equal(DataRightsCaseType.StaffRights, gate.Request!.CaseType);
        Assert.Null(gate.Request.PropertyId);
    }

    [Fact]
    public async Task Exact_replay_returns_original_receipt_after_later_profile_change()
    {
        StaffMember member = CreateMember();
        RecordingExecutionGate gate = new();
        InMemoryReceiptRepository receipts = new();
        ApplyStaffDataRightsCorrectionCommandHandler handler = CreateHandler(
            member,
            receipts,
            gate);
        ApplyStaffDataRightsCorrectionCommand command =
            Command(member, member.Version) with { DisplayName = "Ada Lovelace" };
        Result<StaffDataRightsCorrectionReceiptDto> first =
            await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(member.UpdateProfile(
            "Ada Later",
            member.LegalName,
            member.WorkEmail,
            member.WorkPhone,
            member.EmployeeNumber,
            member.JobTitle,
            member.Department,
            member.Version,
            "user:manager",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);

        Result<StaffDataRightsCorrectionReceiptDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<StaffDataRightsCorrectionReceiptDto> conflict =
            await handler.HandleAsync(
                command with { DisplayName = "Different request" },
                CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value.ReceiptId, replay.Value.ReceiptId);
        Assert.Equal(first.Value.ExecutionId, replay.Value.ExecutionId);
        Assert.Equal(first.Value.CaseId, replay.Value.CaseId);
        Assert.Equal(first.Value.StaffMemberId, replay.Value.StaffMemberId);
        Assert.Equal(first.Value.SelectedRecordVersion, replay.Value.SelectedRecordVersion);
        Assert.Equal(first.Value.CurrentRecordVersion, replay.Value.CurrentRecordVersion);
        Assert.Equal(first.Value.ChangedFieldKeys, replay.Value.ChangedFieldKeys);
        Assert.Equal(first.Value.CompletedAtUtc, replay.Value.CompletedAtUtc);
        Assert.Equal(
            StaffApplicationErrors.CorrectionIdempotencyConflict,
            conflict.Error);
        Assert.Equal(1, gate.EvaluationCount);
    }

    [Fact]
    public async Task Receipt_committed_while_waiting_for_lock_is_replayed()
    {
        StaffMember member = CreateMember();
        ApplyStaffDataRightsCorrectionCommand command =
            Command(member, member.Version) with
            {
                DisplayName = "Ada Lovelace"
            };
        StaffProfileCorrection requested = StaffProfileCorrection.Create(
            command.DisplayName,
            command.LegalName,
            command.WorkEmail,
            command.WorkPhone,
            command.EmployeeNumber,
            command.JobTitle,
            command.Department).Value;
        StaffDataRightsCorrectionReceipt committed =
            StaffDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                member.ScopeId,
                command.ExecutionId,
                command.CaseId,
                command.ApprovalRevision,
                member.Id,
                command.ExpectedVersion,
                command.ExpectedVersion + 1,
                [StaffProfileField.DisplayName],
                StaffDataRightsCorrectionFingerprint.Compute(requested),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now).Value;
        InMemoryReceiptRepository receipts = new(committed);
        ApplyStaffDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                member,
                receipts,
                new RecordingExecutionGate());

        Result<StaffDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(committed.Id, result.Value.ReceiptId);
        Assert.Equal(2, receipts.LookupCount);
        Assert.Equal(command.ExpectedVersion, member.Version);
        Assert.Equal("Ada", member.DisplayName);
    }

    [Fact]
    public async Task Denied_or_no_op_correction_does_not_create_receipt()
    {
        StaffMember member = CreateMember();
        RecordingExecutionGate deniedGate = new(allowed: false);
        InMemoryReceiptRepository receipts = new();
        ApplyStaffDataRightsCorrectionCommandHandler denied = CreateHandler(
            member,
            receipts,
            deniedGate);
        ApplyStaffDataRightsCorrectionCommand changed =
            Command(member, member.Version) with { DisplayName = "Ada Lovelace" };

        Result<StaffDataRightsCorrectionReceiptDto> deniedResult =
            await denied.HandleAsync(changed, CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.DataRightsApprovalRequired,
            deniedResult.Error);
        Assert.Equal(1, member.Version);
        Assert.Null(receipts.Receipt);

        ApplyStaffDataRightsCorrectionCommandHandler allowed = CreateHandler(
            member,
            receipts,
            new RecordingExecutionGate());
        Result<StaffDataRightsCorrectionReceiptDto> noOp =
            await allowed.HandleAsync(
                Command(member, member.Version),
                CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.CorrectionNoChanges, noOp.Error);
        Assert.Null(receipts.Receipt);
    }

    private static ApplyStaffDataRightsCorrectionCommandHandler CreateHandler(
        StaffMember member,
        InMemoryReceiptRepository receipts,
        RecordingExecutionGate gate) => new(
        new InMemoryMemberRepository(member),
        receipts,
        new NoopStaffOperationLock(),
        gate,
        new TestScopeContext(),
        new TestClock(),
        new TestIdGenerator());

    private static ApplyStaffDataRightsCorrectionCommand Command(
        StaffMember member,
        long expectedVersion) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        4,
        member.Id,
        expectedVersion,
        member.DisplayName,
        member.LegalName,
        member.WorkEmail,
        member.WorkPhone,
        member.EmployeeNumber,
        member.JobTitle,
        member.Department,
        "user:privacy");

    private static StaffMember CreateMember() => StaffMember.Create(
        Guid.NewGuid(),
        "tenant-a",
        "Ada",
        legalName: null,
        "ada@example.test",
        workPhone: null,
        "EMP-1",
        "Manager",
        "Operations",
        "account-1",
        "user:owner",
        Guid.NewGuid(),
        Now.AddHours(-1)).Value;

    private sealed class RecordingExecutionGate(bool allowed = true)
        : IDataRightsCorrectionExecutionGate
    {
        public DataRightsCorrectionExecutionGateRequest? Request { get; private set; }
        public int EvaluationCount { get; private set; }

        public Task<DataRightsCorrectionExecutionGateResult> EvaluateAsync(
            DataRightsCorrectionExecutionGateRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            this.EvaluationCount++;
            return Task.FromResult(allowed
                ? DataRightsCorrectionExecutionGateResult.Allowed(Now.AddMinutes(5))
                : DataRightsCorrectionExecutionGateResult.Denied(
                    DataRightsCorrectionExecutionDenial.ExecutionNotFound));
        }
    }

    private sealed class InMemoryReceiptRepository(
        StaffDataRightsCorrectionReceipt? delayedReceipt = null)
        : IStaffDataRightsCorrectionReceiptRepository
    {
        public StaffDataRightsCorrectionReceipt? Receipt { get; private set; }
        public int LookupCount { get; private set; }

        public Task<StaffDataRightsCorrectionReceipt?> FindByExecutionIdAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            this.LookupCount++;
            StaffDataRightsCorrectionReceipt? visible =
                this.Receipt ??
                (this.LookupCount >= 2 ? delayedReceipt : null);
            return Task.FromResult(
                visible?.ExecutionId == executionId
                    ? visible
                    : null);
        }

        public Task AddAsync(
            StaffDataRightsCorrectionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryMemberRepository(StaffMember member)
        : IStaffMemberRepository
    {
        public Task AddAsync(
            StaffMember ignored,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(member.Id == staffMemberId ? member : null);

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
            Task.FromResult(false);

        public Task<bool> AuthSubjectExistsAsync(
            string authSubjectId,
            Guid? exceptStaffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => "tenant-a";
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
