namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed partial class ExecuteDataRightsRestrictionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_exact_subject_completes_with_owner_proof()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        RecordingApprovalGate gate = new(DataRightsOperationApprovalResult.Approved);
        RecordingContributor contributor = new(CompletedOwnerResult(
            effectiveRestricted: true));
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            gate,
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);
        ExecuteDataRightsRestrictionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:executor");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsCaseStatus.Completed, result.Value.Case.Status);
        Assert.True(result.Value.Proof.EffectiveRestricted);
        Assert.Equal(
            result.Value.Proof,
            result.Value.Case.RestrictionExecutionProof);
        Assert.Equal(1, contributor.CallCount);
        Assert.Equal(DataRightsOperation.Restriction, gate.Request?.Operation);
        Assert.Equal(
            DataRightsRestrictionDirective.Apply,
            gate.Request?.RestrictionDirective);
        Assert.Equal(
            dataRightsCase.SelectedSubjects.Single().RecordId,
            contributor.Request?.Coordinate.RecordId);
    }

    [Fact]
    public async Task Equivalent_retry_returns_committed_proof_without_owner_call()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Release);
        RecordingContributor contributor = new(CompletedOwnerResult(
            effectiveRestricted: true,
            dataRightsCase.RestrictionReleaseTarget));
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Approved),
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);
        ExecuteDataRightsRestrictionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:executor");

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var replay = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.Proof, replay.Value.Proof);
        Assert.True(first.Value.Proof.EffectiveRestricted);
        Assert.Equal(1, contributor.CallCount);
        Assert.Equal(
            dataRightsCase.RestrictionReleaseTarget!.OwnerOperationId,
            contributor.Request?.TargetOwnerOperationId);
        Assert.Equal(
            dataRightsCase.RestrictionReleaseTarget.OwnerOperationVersion,
            contributor.Request?.TargetOwnerOperationVersion);
    }

    [Fact]
    public async Task Changed_retry_conflicts_without_owner_call()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        RecordingContributor contributor = new(CompletedOwnerResult(
            effectiveRestricted: true));
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Approved),
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);
        ExecuteDataRightsRestrictionCommand command = new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:executor");
        Assert.True((await handler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);

        var conflict = await handler.HandleAsync(
            command with { IdempotencyKey = Guid.NewGuid() },
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionExecutionConflict,
            conflict.Error);
        Assert.Equal(1, contributor.CallCount);
    }

    [Fact]
    public async Task Approval_denial_leaves_case_approved_and_owner_untouched()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        RecordingContributor contributor = new(CompletedOwnerResult(
            effectiveRestricted: true));
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Denied(
                DataRightsOperationApprovalDenial.SubjectNotApproved)),
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

        var denied = await handler.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionExecutionDenied,
            denied.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
        Assert.Equal(0, contributor.CallCount);
    }

    [Fact]
    public async Task Tenant_scoped_staff_restriction_preserves_absent_property()
    {
        DataRightsCase dataRightsCase = CreateApprovedStaffCase(
            DataRightsRestrictionAction.Apply);
        RecordingApprovalGate gate = new(DataRightsOperationApprovalResult.Approved);
        RecordingContributor contributor = new(
            CompletedOwnerResult(effectiveRestricted: true),
            "staff");
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            gate,
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new ExecuteDataRightsRestrictionCommand(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsCaseType.StaffRights, gate.Request?.CaseType);
        Assert.Null(gate.Request?.PropertyId);
        Assert.Equal(DataRightsCaseType.StaffRights, contributor.Request?.CaseType);
        Assert.Null(contributor.Request?.PropertyId);
    }

    [Fact]
    public async Task Unexpected_owner_exception_preserves_case_and_requests_retry()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        RecordingContributor contributor = new(
            CompletedOwnerResult(effectiveRestricted: true),
            exception: new TimeoutException());
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Approved),
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
    }

    [Fact]
    public async Task Owner_result_returned_at_deadline_preserves_case_and_requests_retry()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        MutableClock clock = new(Now);
        RecordingContributor contributor = new(
            CompletedOwnerResult(effectiveRestricted: true),
            onExecute: request => clock.UtcNow = request.DeadlineUtc);
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Approved),
            [contributor],
            clock,
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
        Assert.Equal(1, contributor.CallCount);
    }

    [Fact]
    public async Task Owner_proof_completed_at_deadline_is_rejected()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        RecordingContributor contributor = new(CompletedOwnerResult(
            effectiveRestricted: true,
            completedAtUtc: Now.AddSeconds(30)));
        ExecuteDataRightsRestrictionCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(DataRightsOperationApprovalResult.Approved),
            [contributor],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:executor"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerProofInvalid,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
        Assert.Equal(1, contributor.CallCount);
    }

    private static DataRightsRestrictionContributionResult CompletedOwnerResult(
        bool effectiveRestricted,
        BunkFy.Modules.DataRights.Domain.ValueObjects
            .DataRightsRestrictionReleaseTarget? releaseTarget = null,
        DateTimeOffset? completedAtUtc = null) =>
        DataRightsRestrictionContributionResult.Completed(
            new(
                ReceiptContractVersion: 1,
                Guid.NewGuid(),
                releaseTarget?.OwnerOperationId ?? Guid.NewGuid(),
                ResultingOwnerRevision:
                    releaseTarget is null
                        ? 2
                        : releaseTarget.OwnerOperationVersion + 1,
                ResultingProjectionRevision: 4,
                effectiveRestricted,
                new string('a', 64),
                completedAtUtc ?? Now.AddSeconds(-5)));

    private static DataRightsCase CreateApprovedCase(
        DataRightsRestrictionAction action)
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            action).Value;
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
        if (action == DataRightsRestrictionAction.Release)
        {
            Assert.True(dataRightsCase.SelectRestrictionReleaseTarget(
                "guests",
                Guid.NewGuid(),
                ownerOperationVersion: 7,
                dataRightsCase.Version,
                "user:privacy",
                Now.AddMinutes(-3).AddSeconds(-30)).IsSuccess);
        }
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

    private static DataRightsCase CreateApprovedStaffCase(
        DataRightsRestrictionAction action)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            action).Value;
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
                caseId == dataRightsCase.Id &&
                scope.CaseType == (DataRightsCaseType)dataRightsCase.Kind &&
                scope.PropertyId == dataRightsCase.PropertyId
                    ? dataRightsCase
                    : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingApprovalGate(
        DataRightsOperationApprovalResult result)
        : IDataRightsOperationApprovalGate
    {
        public DataRightsOperationApprovalRequest? Request { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingContributor(
        DataRightsRestrictionContributionResult result,
        string ownerKey = "guests",
        Exception? exception = null,
        Action<DataRightsRestrictionContributionRequest>? onExecute = null)
        : IDataRightsRestrictionContributor
    {
        public string OwnerKey => ownerKey;
        public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;
        public int CallCount { get; private set; }
        public DataRightsRestrictionContributionRequest? Request { get; private set; }

        public Task<DataRightsRestrictionTargetResolutionResult>
            ResolveReleaseTargetsAsync(
                DataRightsRestrictionTargetResolutionRequest request,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsRestrictionContributionResult> ExecuteAsync(
            DataRightsRestrictionContributionRequest request,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            this.Request = request;
            onExecute?.Invoke(request);
            if (exception is not null)
            {
                throw exception;
            }
            return Task.FromResult(result);
        }
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
