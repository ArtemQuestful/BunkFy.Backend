namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Authorization;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Observability;
using Gma.Framework.Pagination;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsOperationApprovalGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_approved_case_operation_subject_and_revision_are_authorized()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(propertyId, recordId);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);

        DataRightsOperationApprovalResult result = await gate.EvaluateAsync(
            CreateRequest(dataRightsCase, propertyId, recordId),
            CancellationToken.None);

        Assert.True(result.IsApproved);
        Assert.Equal(DataRightsOperationApprovalDenial.None, result.Denial);
        Assert.Empty(securitySignals.Definitions);
    }

    [Fact]
    public async Task Normal_case_return_after_cancellation_is_not_authorized()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(propertyId, recordId);
        using CancellationTokenSource source = new();
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(
                dataRightsCase,
                _ => source.Cancel()),
            securitySignals);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            gate.EvaluateAsync(
                CreateRequest(dataRightsCase, propertyId, recordId),
                source.Token));

        Assert.Empty(securitySignals.Definitions);
    }

    [Fact]
    public async Task Restriction_approval_authorizes_only_its_exact_directive()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(
            propertyId,
            recordId,
            DataRightsCaseOperation.Restriction,
            DataRightsRestrictionAction.Apply);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);
        DataRightsOperationApprovalRequest applyRequest = CreateRequest(
            dataRightsCase,
            propertyId,
            recordId) with
        {
            Operation = DataRightsOperation.Restriction,
            RestrictionDirective = DataRightsRestrictionDirective.Apply
        };

        DataRightsOperationApprovalResult approved = await gate.EvaluateAsync(
            applyRequest,
            CancellationToken.None);
        DataRightsOperationApprovalResult mismatched = await gate.EvaluateAsync(
            applyRequest with { RestrictionDirective = DataRightsRestrictionDirective.Release },
            CancellationToken.None);
        DataRightsOperationApprovalResult unknown = await gate.EvaluateAsync(
            applyRequest with { RestrictionDirective = DataRightsRestrictionDirective.Unknown },
            CancellationToken.None);

        Assert.True(approved.IsApproved);
        Assert.Equal(
            DataRightsOperationApprovalDenial.RestrictionDirectiveMismatch,
            mismatched.Denial);
        Assert.Equal(DataRightsOperationApprovalDenial.InvalidRequest, unknown.Denial);
        Assert.Equal(2, securitySignals.Definitions.Count);
        Assert.All(
            securitySignals.Definitions,
            definition => Assert.Equal(
                "data-rights.operation-approval-denied",
                definition.Code));
    }

    [Fact]
    public async Task Restriction_release_approval_binds_the_exact_owner_target()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(
            propertyId,
            recordId,
            DataRightsCaseOperation.Restriction,
            DataRightsRestrictionAction.Release);
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            new RecordingSecuritySignalRecorder());
        DataRightsOperationApprovalRequest request = CreateRequest(
            dataRightsCase,
            propertyId,
            recordId) with
        {
            Operation = DataRightsOperation.Restriction,
            RestrictionDirective = DataRightsRestrictionDirective.Release,
            RestrictionTargetOwnerOperationId =
                dataRightsCase.RestrictionReleaseTarget!.OwnerOperationId,
            RestrictionTargetOwnerOperationVersion =
                dataRightsCase.RestrictionReleaseTarget.OwnerOperationVersion
        };

        DataRightsOperationApprovalResult approved = await gate.EvaluateAsync(
            request,
            CancellationToken.None);
        DataRightsOperationApprovalResult substituted = await gate.EvaluateAsync(
            request with { RestrictionTargetOwnerOperationId = Guid.NewGuid() },
            CancellationToken.None);
        DataRightsOperationApprovalResult unbound = await gate.EvaluateAsync(
            request with
            {
                RestrictionTargetOwnerOperationId = null,
                RestrictionTargetOwnerOperationVersion = null
            },
            CancellationToken.None);

        Assert.True(approved.IsApproved);
        Assert.Equal(
            DataRightsOperationApprovalDenial.RestrictionTargetMismatch,
            substituted.Denial);
        Assert.Equal(
            DataRightsOperationApprovalDenial.RestrictionTargetMismatch,
            unbound.Denial);
    }

    [Theory]
    [InlineData("tenant-b", 6, DataRightsOperation.Correction, 3, DataRightsOperationApprovalDenial.CaseNotFound)]
    [InlineData("tenant-a", 5, DataRightsOperation.Correction, 3, DataRightsOperationApprovalDenial.ApprovalRevisionMismatch)]
    [InlineData("tenant-a", 6, DataRightsOperation.Erasure, 3, DataRightsOperationApprovalDenial.OperationNotApproved)]
    [InlineData("tenant-a", 6, DataRightsOperation.Correction, 4, DataRightsOperationApprovalDenial.SubjectNotApproved)]
    public async Task Approval_gate_fails_closed_on_every_execution_coordinate_mismatch(
        string tenantId,
        long approvalRevision,
        DataRightsOperation operation,
        long recordVersion,
        DataRightsOperationApprovalDenial expectedDenial)
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(propertyId, recordId);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);
        DataRightsOperationApprovalRequest request = CreateRequest(
            dataRightsCase,
            propertyId,
            recordId) with
        {
            TenantId = tenantId,
            ApprovalRevision = approvalRevision,
            Operation = operation,
            RecordVersion = recordVersion
        };

        DataRightsOperationApprovalResult result = await gate.EvaluateAsync(
            request,
            CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(expectedDenial, result.Denial);
        Assert.Equal(
            "data-rights.operation-approval-denied",
            Assert.Single(securitySignals.Definitions).Code);
    }

    [Fact]
    public async Task Approval_gate_rejects_combined_operations_as_an_invalid_request()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(propertyId, recordId);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);
        DataRightsOperationApprovalRequest request = CreateRequest(
            dataRightsCase,
            propertyId,
            recordId) with
        {
            Operation = DataRightsOperation.AccessExport | DataRightsOperation.Correction
        };

        DataRightsOperationApprovalResult result = await gate.EvaluateAsync(
            request,
            CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(DataRightsOperationApprovalDenial.InvalidRequest, result.Denial);
        Assert.Equal(
            "data-rights.operation-approval-denied",
            Assert.Single(securitySignals.Definitions).Code);
    }

    [Fact]
    public async Task Denied_case_cannot_authorize_owner_execution()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDecisionPendingCase(propertyId, recordId);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Denied,
            DataRightsCaseDecisionReason.RequestInvalid,
            5,
            "user:operator-b",
            Now.AddMinutes(5)).IsSuccess);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);

        DataRightsOperationApprovalResult result = await gate.EvaluateAsync(
            CreateRequest(dataRightsCase, propertyId, recordId),
            CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(DataRightsOperationApprovalDenial.CaseNotApproved, result.Denial);
        Assert.Equal(
            "data-rights.operation-approval-denied",
            Assert.Single(securitySignals.Definitions).Code);
    }

    [Fact]
    public async Task Anonymisation_returns_frozen_evidence_only_to_a_distinct_executor()
    {
        Guid propertyId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateApprovedCase(
            propertyId,
            recordId,
            DataRightsCaseOperation.Anonymisation);
        RecordingSecuritySignalRecorder securitySignals = new();
        DataRightsOperationApprovalGate gate = new(
            new StubCaseRepository(dataRightsCase),
            securitySignals);
        DataRightsOperationApprovalRequest request = CreateRequest(
            dataRightsCase,
            propertyId,
            recordId) with
        {
            Operation = DataRightsOperation.Anonymisation,
            ExecutingActorId = "user:executor"
        };

        DataRightsOperationApprovalResult approved = await gate.EvaluateAsync(
            request,
            CancellationToken.None);
        DataRightsOperationApprovalResult sameActor = await gate.EvaluateAsync(
            request with { ExecutingActorId = "user:operator-b" },
            CancellationToken.None);
        DataRightsOperationApprovalResult missingActor = await gate.EvaluateAsync(
            request with { ExecutingActorId = null },
            CancellationToken.None);

        Assert.True(approved.IsApproved);
        Assert.NotNull(approved.ApprovalEvidence);
        Assert.Equal("approved-policy", approved.ApprovalEvidence.PolicyId);
        Assert.Equal(
            DataRightsOperationApprovalDenial.DecisionActorCannotExecute,
            sameActor.Denial);
        Assert.Equal(
            DataRightsOperationApprovalDenial.ExecutionActorRequired,
            missingActor.Denial);
        Assert.Equal(2, securitySignals.Definitions.Count);
    }

    private static DataRightsCase CreateApprovedCase(
        Guid propertyId,
        Guid recordId,
        DataRightsCaseOperation operations =
            DataRightsCaseOperation.AccessExport | DataRightsCaseOperation.Correction,
        DataRightsRestrictionAction restrictionAction = DataRightsRestrictionAction.None)
    {
        DataRightsCase dataRightsCase = CreateDecisionPendingCase(
            propertyId,
            recordId,
            operations,
            restrictionAction);
        DataRightsApprovalPolicyEvidence? evidence =
            operations == DataRightsCaseOperation.Anonymisation
                ? DataRightsApprovalPolicyEvidence.Create(
                    propertyId,
                    9,
                    "GB",
                    "approved-policy",
                    3,
                    "guest-retention",
                    2,
                    new string('a', 64),
                    "data-rights-anonymisation",
                    "erasure",
                    "authorized-workspace-operator",
                    Now.AddMinutes(5)).Value
                : null;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:operator-b",
            Now.AddMinutes(5),
            evidence).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateDecisionPendingCase(
        Guid propertyId,
        Guid recordId,
        DataRightsCaseOperation operations =
            DataRightsCaseOperation.AccessExport | DataRightsCaseOperation.Correction,
        DataRightsRestrictionAction restrictionAction = DataRightsRestrictionAction.None)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            operations,
            DataRightsRequesterRelation.ControllerInitiated,
            restrictionAction).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator-a",
            Now).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            recordId,
            3,
            dataRightsCase.Version,
            "user:operator-a",
            Now.AddMinutes(2)).IsSuccess);
        if (restrictionAction == DataRightsRestrictionAction.Release)
        {
            Assert.True(dataRightsCase.SelectRestrictionReleaseTarget(
                "guests",
                Guid.NewGuid(),
                ownerOperationVersion: 4,
                dataRightsCase.Version,
                "user:operator-a",
                Now.AddMinutes(2).AddSeconds(30)).IsSuccess);
        }
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:operator-a",
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:operator-b",
            Now.AddMinutes(4)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsOperationApprovalRequest CreateRequest(
        DataRightsCase dataRightsCase,
        Guid propertyId,
        Guid recordId) => new(
            "tenant-a",
            propertyId,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision!.Value,
            DataRightsOperation.Correction,
            "guests",
            "guest-profile",
            recordId,
            3);

    private sealed class StubCaseRepository(
        DataRightsCase dataRightsCase,
        Action<CancellationToken>? afterGet = null)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase ignored,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken)
        {
            DataRightsCase? result =
                dataRightsCase.PropertyId == scope.PropertyId &&
                dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
                dataRightsCase.Id == caseId
                    ? dataRightsCase
                    : null;
            afterGet?.Invoke(cancellationToken);
            return Task.FromResult(result);
        }

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingSecuritySignalRecorder
        : ISecuritySignalRecorder
    {
        public List<SecuritySignalDefinition> Definitions { get; } = [];

        public SecuritySignalReceipt Record(
            SecuritySignalDefinition definition,
            Guid? correlationId = null)
        {
            this.Definitions.Add(definition);
            return new(
                correlationId?.ToString("N") ?? new string('0', 32),
                true);
        }
    }
}
