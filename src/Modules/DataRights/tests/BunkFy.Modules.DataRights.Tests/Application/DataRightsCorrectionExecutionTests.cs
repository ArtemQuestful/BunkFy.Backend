namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Authorization;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;
using DomainSubject = BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

[Trait("Category", "Unit")]
public sealed class DataRightsCorrectionExecutionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_claims_one_exact_owner_and_same_request_replays()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        StubCorrectionExecutionRepository executions = new();
        MutableClock clock = new(Now);
        StartDataRightsCorrectionExecutionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new GuestCorrectionPolicy()],
            clock);
        Guid executionId = Guid.NewGuid();
        StartDataRightsCorrectionExecutionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            executionId,
            dataRightsCase.Version,
            "user:executor");

        Result<DataRightsCorrectionExecutionDto> first =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<DataRightsCorrectionExecutionDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(DataRightsCaseStatus.Executing, first.Value.Case.Status);
        Assert.Equal(executionId, first.Value.Execution.ExecutionId);
        Assert.Equal(
            GuestCorrectionPolicy.PolicyKey,
            first.Value.Execution.FieldPolicyKey);
        Assert.Equal("user:executor", first.Value.Execution.ClaimedBy);
        Assert.True(first.Value.Execution.IsCurrentActor);
        Assert.Equal(first.Value.Execution, replay.Value.Execution);
        Assert.Equal(1, executions.AddCount);

        GetDataRightsCorrectionExecutionQueryHandler reader = new(executions);
        var viewedByOther = await reader.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId.Value),
                dataRightsCase.Id,
                "user:other"),
            CancellationToken.None);
        Assert.False(viewedByOther.Value.IsCurrentActor);
    }

    [Fact]
    public async Task Expired_claim_can_be_taken_over_but_active_claim_cannot()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        StubCorrectionExecutionRepository executions = new();
        MutableClock clock = new(Now);
        StartDataRightsCorrectionExecutionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new GuestCorrectionPolicy()],
            clock);
        Guid executionId = Guid.NewGuid();
        long selectedCaseVersion = dataRightsCase.Version;
        StartDataRightsCorrectionExecutionCommand initial = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            executionId,
            selectedCaseVersion,
            "user:executor");
        Assert.True((await handler.HandleAsync(
            initial,
            CancellationToken.None)).IsSuccess);

        Result<DataRightsCorrectionExecutionDto> activeTakeover =
            await handler.HandleAsync(
                initial with { ActorId = "user:other" },
                CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(10);
        Result<DataRightsCorrectionExecutionDto> expiredTakeover =
            await handler.HandleAsync(
                initial with { ActorId = "user:other" },
                CancellationToken.None);
        Result<DataRightsCorrectionExecutionDto> previousActorRetry =
            await handler.HandleAsync(initial, CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.CorrectionExecutionConflict,
            activeTakeover.Error);
        Assert.True(expiredTakeover.IsSuccess, expiredTakeover.Error.Code);
        Assert.Equal("user:other", expiredTakeover.Value.Execution.ClaimedBy);
        Assert.True(expiredTakeover.Value.Execution.IsCurrentActor);
        Assert.Equal(
            DataRightsApplicationErrors.CorrectionExecutionConflict,
            previousActorRetry.Error);
        Assert.Equal(1, executions.AddCount);
    }

    [Fact]
    public async Task Tenant_staff_claim_gate_and_completion_preserve_tenant_scope()
    {
        DataRightsCase dataRightsCase = CreateApprovedStaffCase();
        StubCorrectionExecutionRepository executions = new();
        MutableClock clock = new(Now);
        StartDataRightsCorrectionExecutionCommandHandler starter = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new StaffCorrectionPolicy()],
            clock);
        Guid executionId = Guid.NewGuid();
        Result<DataRightsCorrectionExecutionDto> started =
            await starter.HandleAsync(
                new(
                    DataRightsCaseScope.Staff,
                    dataRightsCase.Id,
                    executionId,
                    dataRightsCase.Version,
                    "user:executor"),
                CancellationToken.None);
        DomainSubject subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionExecutionGate gate = new(
            new StubCaseRepository(dataRightsCase),
            executions,
            clock);

        DataRightsCorrectionExecutionGateResult allowed =
            await gate.EvaluateAsync(
                new(
                    dataRightsCase.ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    dataRightsCase.Id,
                    dataRightsCase.DecisionRevision!.Value,
                    executionId,
                    new(
                        subject.OwnerKey,
                        subject.RecordType,
                        subject.RecordId,
                        subject.RecordVersion),
                    StaffCorrectionPolicy.PolicyKey,
                    "user:executor"),
                CancellationToken.None);

        Assert.True(started.IsSuccess, started.Error.Code);
        Assert.Equal(DataRightsCaseType.StaffRights, started.Value.Execution.CaseType);
        Assert.Null(started.Value.Execution.PropertyId);
        Assert.True(allowed.IsAllowed);

        DataRightsTenantCorrectionAppliedIntegrationEvent applied = new(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            Now.AddMinutes(1),
            executionId,
            DataRightsCaseType.StaffRights,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision.Value,
            subject.OwnerKey,
            subject.RecordType,
            subject.RecordId,
            subject.RecordVersion,
            subject.RecordVersion + 1,
            StaffCorrectionPolicy.PolicyKey,
            receiptContractVersion: 1,
            Guid.NewGuid(),
            ["staff.profile.display-name"]);
        DataRightsCorrectionCompletionCoordinator coordinator = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            new MutableClock(Now.AddSeconds(30)));
        DataRightsTenantCorrectionAppliedIntegrationEvent futureApplied = new(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            Now.AddMinutes(2),
            executionId,
            DataRightsCaseType.StaffRights,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision.Value,
            subject.OwnerKey,
            subject.RecordType,
            subject.RecordId,
            subject.RecordVersion,
            subject.RecordVersion + 1,
            StaffCorrectionPolicy.PolicyKey,
            receiptContractVersion: 1,
            applied.ReceiptId,
            ["staff.profile.display-name"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.CompleteAsync(
                futureApplied,
                CancellationToken.None));
        await coordinator.CompleteAsync(applied, CancellationToken.None);

        Assert.Equal(DataRightsCaseState.Completed, dataRightsCase.Status);
        Assert.Equal(
            DataRightsCorrectionExecutionState.Completed,
            executions.Execution!.State);
    }

    [Fact]
    public async Task Replay_fails_closed_when_case_no_longer_matches_claim_revision()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        StubCorrectionExecutionRepository executions = new();
        StartDataRightsCorrectionExecutionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new GuestCorrectionPolicy()],
            new MutableClock(Now));
        StartDataRightsCorrectionExecutionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:executor");
        Assert.True((await handler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Assert.True(dataRightsCase.CompleteCorrectionExecution(
            dataRightsCase.Version,
            dataRightsCase.DecisionRevision!.Value,
            "system:test",
            Now.AddSeconds(1),
            Now.AddSeconds(1)).IsSuccess);

        Result<DataRightsCorrectionExecutionDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.CorrectionExecutionConflict,
            replay.Error);
    }

    [Fact]
    public async Task Gate_requires_exact_actor_policy_subject_and_unexpired_claim()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        StubCorrectionExecutionRepository executions = new();
        MutableClock clock = new(Now);
        StartDataRightsCorrectionExecutionCommandHandler starter = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new GuestCorrectionPolicy()],
            clock);
        Guid executionId = Guid.NewGuid();
        Assert.True((await starter.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                executionId,
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None)).IsSuccess);
        DomainSubject subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionExecutionGate gate = new(
            new StubCaseRepository(dataRightsCase),
            executions,
            clock);
        DataRightsCorrectionExecutionGateRequest request = new(
            dataRightsCase.ScopeId,
            DataRightsCaseType.GuestRights,
            dataRightsCase.PropertyId.Value,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision!.Value,
            executionId,
            new(
                subject.OwnerKey,
                subject.RecordType,
                subject.RecordId,
                subject.RecordVersion),
            GuestCorrectionPolicy.PolicyKey,
            "user:executor");

        DataRightsCorrectionExecutionGateResult allowed =
            await gate.EvaluateAsync(request, CancellationToken.None);
        DataRightsCorrectionExecutionGateResult changedActor =
            await gate.EvaluateAsync(
                request with { ExecutingActorId = "user:other" },
                CancellationToken.None);
        DataRightsCorrectionExecutionGateResult changedPolicy =
            await gate.EvaluateAsync(
                request with { FieldPolicyKey = "guests.other.correction.v1" },
                CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(10);
        DataRightsCorrectionExecutionGateResult expired =
            await gate.EvaluateAsync(request, CancellationToken.None);

        Assert.True(allowed.IsAllowed);
        Assert.Equal(
            DataRightsCorrectionExecutionDenial.ExecutionMismatch,
            changedActor.Denial);
        Assert.Equal(
            DataRightsCorrectionExecutionDenial.ExecutionMismatch,
            changedPolicy.Denial);
        Assert.Equal(
            DataRightsCorrectionExecutionDenial.ExecutionExpired,
            expired.Denial);
    }

    [Fact]
    public async Task Owner_completion_converges_case_and_claim_once_with_exact_proof()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        StubCorrectionExecutionRepository executions = new();
        MutableClock clock = new(Now.AddMinutes(11));
        Guid executionId = Guid.NewGuid();
        StartDataRightsCorrectionExecutionCommandHandler starter = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            [new GuestCorrectionPolicy()],
            new MutableClock(Now));
        StartDataRightsCorrectionExecutionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            executionId,
            dataRightsCase.Version,
            "user:executor");
        Assert.True((await starter.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        DomainSubject subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionAppliedIntegrationEvent applied = new(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            Now.AddMinutes(10).AddSeconds(1),
            executionId,
            dataRightsCase.PropertyId.Value,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision!.Value,
            subject.OwnerKey,
            subject.RecordType,
            subject.RecordId,
            subject.RecordVersion,
            subject.RecordVersion + 1,
            GuestCorrectionPolicy.PolicyKey,
            receiptContractVersion: 1,
            Guid.NewGuid(),
            ["guest.profile.display-name"]);
        DataRightsCorrectionCompletionCoordinator coordinator = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            executions,
            clock);

        DataRightsCorrectionAppliedIntegrationEvent mismatched = new(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            applied.OccurredAtUtc,
            executionId,
            applied.PropertyId,
            applied.CaseId,
            applied.ApprovalRevision,
            "reservations",
            "reservation",
            applied.RecordId,
            applied.SelectedRecordVersion,
            applied.CurrentRecordVersion,
            "reservations.reservation.correction.v1",
            applied.ReceiptContractVersion,
            Guid.NewGuid(),
            ["reservation.guest.primary-name"]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.CompleteAsync(mismatched, CancellationToken.None));

        await coordinator.CompleteAsync(applied, CancellationToken.None);
        await coordinator.CompleteAsync(applied, CancellationToken.None);
        Result<DataRightsCorrectionExecutionDto> replay =
            await starter.HandleAsync(command, CancellationToken.None);

        Assert.Equal(DataRightsCaseState.Completed, dataRightsCase.Status);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(
            DataRightsCorrectionExecutionStatus.Completed,
            replay.Value.Execution.Status);
        Assert.Equal(
            DataRightsCorrectionExecutionState.Completed,
            executions.Execution!.State);
        Assert.Equal(applied.ReceiptId, executions.Execution.ReceiptId);
        Assert.Equal(applied.ChangedFieldsSha256, executions.Execution.ChangedFieldsSha256);
        Assert.Equal(applied.ReceiptSha256, executions.Execution.ReceiptSha256);
    }

    private static DataRightsCase CreateApprovedCase()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Correction,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            Now.AddMinutes(-6)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-5)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-2)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-1)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateApprovedStaffCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Correction,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            Now.AddMinutes(-6)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-5)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-2)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-1)).IsSuccess);
        return dataRightsCase;
    }

    private sealed class GuestCorrectionPolicy : IDataRightsCorrectionPolicyContributor
    {
        public const string PolicyKey = "guests.guest-profile.correction.v1";

        public int ContractVersion => DataRightsCorrectionContract.CurrentVersion;
        public string OwnerKey => "guests";
        public string RecordType => "guest-profile";
        public string FieldPolicyKey => PolicyKey;
    }

    private sealed class StaffCorrectionPolicy : IDataRightsCorrectionPolicyContributor
    {
        public const string PolicyKey = "staff.staff-member.correction.v1";

        public int ContractVersion => DataRightsCorrectionContract.CurrentVersion;
        public string OwnerKey => "staff";
        public string RecordType => "staff-member";
        public string FieldPolicyKey => PolicyKey;
    }

    private sealed class StubCorrectionExecutionRepository
        : IDataRightsCorrectionExecutionRepository
    {
        public DataRightsCorrectionExecution? Execution { get; private set; }
        public int AddCount { get; private set; }

        public Task AddAsync(
            DataRightsCorrectionExecution execution,
            CancellationToken cancellationToken)
        {
            this.Execution = execution;
            this.AddCount++;
            return Task.CompletedTask;
        }

        public Task<DataRightsCorrectionExecution?> GetAsync(
            Guid caseId,
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Execution?.CaseId == caseId &&
                this.Execution.Id == executionId
                    ? this.Execution
                    : null);

        public Task<DataRightsCorrectionExecution?> GetByCaseAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Execution?.CaseId == caseId
                    ? this.Execution
                    : null);
    }

    private sealed class StubCaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase ignored,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                scope.CaseType == (DataRightsCaseType)dataRightsCase.Kind &&
                scope.PropertyId == dataRightsCase.PropertyId &&
                caseId == dataRightsCase.Id
                    ? dataRightsCase
                    : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
