namespace BunkFy.Modules.DataRights.Tests.Domain;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsCaseTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Guest_case_starts_as_a_versioned_PII_minimal_draft()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = Create(
            propertyId,
            DataRightsRequesterRelation.DataSubject);

        Assert.Equal(propertyId, dataRightsCase.PropertyId);
        Assert.Equal(DataRightsCaseKind.GuestRights, dataRightsCase.Kind);
        Assert.Equal(DataRightsCaseOperation.AccessExport, dataRightsCase.RequestedOperations);
        Assert.Equal(DataRightsVerificationState.Pending, dataRightsCase.VerificationStatus);
        Assert.Equal(DataRightsRoutingState.Pending, dataRightsCase.RoutingStatus);
        Assert.Equal(DataRightsCaseState.Draft, dataRightsCase.Status);
        Assert.Equal(1, dataRightsCase.Version);
        Assert.Equal("user:operator-a", dataRightsCase.CreatedBy);
    }

    [Fact]
    public void Sensitive_discovery_requires_verification_and_controller_routing()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.AuthorizedRepresentative);

        Assert.Equal(
            "DataRights.VerificationRequired",
            dataRightsCase.BeginDiscovery(1, "user:operator-a", Now.AddMinutes(1)).Error.Code);
        Assert.True(dataRightsCase.RecordRequesterVerification(
            verified: true,
            expectedVersion: 1,
            "user:operator-b",
            Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(
            "DataRights.ControllerRoutingRequired",
            dataRightsCase.BeginDiscovery(2, "user:operator-b", Now.AddMinutes(2)).Error.Code);
        Assert.True(dataRightsCase.RecordControllerRouting(
            2,
            "user:operator-b",
            Now.AddMinutes(2),
            CreateDeadlineEvidence(dataRightsCase.PropertyId!.Value, Now.AddMinutes(2))).IsSuccess);
        Assert.True(dataRightsCase.BeginDiscovery(
            3,
            "user:operator-b",
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(4, dataRightsCase.Version);
    }

    [Fact]
    public void External_guest_routing_fails_closed_until_deadline_evidence_is_available()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.DataSubject);

        Result result = dataRightsCase.RecordControllerRouting(
            1,
            "user:operator-b",
            Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal("DataRights.ResponseDeadlinePolicyRequired", result.Error.Code);
        Assert.Equal(DataRightsRoutingState.Pending, dataRightsCase.RoutingStatus);
        Assert.Equal(1, dataRightsCase.Version);
    }

    [Fact]
    public void Deadline_evidence_can_be_frozen_at_intake_and_reused_during_routing()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.DataSubject).Value;
        DataRightsResponseDeadlinePolicyEvidence evidence =
            CreateDeadlineEvidence(propertyId, Now);
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator-a",
            Now,
            evidence).Value;

        Assert.Equal(evidence.DueAtUtc, dataRightsCase.DueAtUtc);
        Assert.Same(evidence, dataRightsCase.ResponseDeadlinePolicyEvidence);
        Assert.True(dataRightsCase.RecordControllerRouting(
            1,
            "user:operator-b",
            Now.AddMinutes(1)).IsSuccess);
    }

    [Fact]
    public void Deadline_evidence_for_another_property_is_rejected()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            Guid.NewGuid(),
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.DataSubject).Value;

        Result<DataRightsCase> result = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator-a",
            Now,
            CreateDeadlineEvidence(Guid.NewGuid(), Now));

        Assert.True(result.IsFailure);
        Assert.Equal(
            "DataRights.ResponseDeadlinePolicyEvidenceInvalid",
            result.Error.Code);
    }

    [Fact]
    public void Controller_initiated_case_can_start_discovery_without_fake_verification()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.ControllerInitiated);

        Assert.Equal(DataRightsVerificationState.NotRequired, dataRightsCase.VerificationStatus);
        Assert.Equal(DataRightsRoutingState.NotRequired, dataRightsCase.RoutingStatus);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
    }

    [Fact]
    public void Review_and_cancel_are_explicit_versioned_transitions()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.ControllerInitiated);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            1,
            2,
            "user:operator-b",
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator-b",
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(
            "DataRights.VersionConflict",
            dataRightsCase.Cancel(3, "user:operator-b", Now.AddMinutes(4)).Error.Code);
        Assert.True(dataRightsCase.Cancel(
            4,
            "user:operator-b",
            Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(DataRightsCaseState.Canceled, dataRightsCase.Status);
        Assert.Equal(5, dataRightsCase.Version);
    }

    [Fact]
    public void Review_requires_a_selected_subject_coordinate()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.ControllerInitiated);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(
            "DataRights.SubjectSelectionRequired",
            dataRightsCase.RequireReview(
                2,
                "user:operator-a",
                Now.AddMinutes(2)).Error.Code);
    }

    [Fact]
    public void Approval_is_an_explicit_attributable_and_immutable_revision()
    {
        DataRightsCase dataRightsCase = CreateReviewRequired();

        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(DataRightsCaseState.DecisionPending, dataRightsCase.Status);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            " user:decision-maker ",
            Now.AddMinutes(5)).IsSuccess);

        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Equal(DataRightsCaseDecision.Approved, dataRightsCase.Decision);
        Assert.Equal(DataRightsCaseDecisionReason.RequestValidated, dataRightsCase.DecisionReason);
        Assert.Equal(6, dataRightsCase.DecisionRevision);
        Assert.Equal(dataRightsCase.Version, dataRightsCase.DecisionRevision);
        Assert.Equal("user:decision-maker", dataRightsCase.DecidedBy);
        Assert.Equal(Now.AddMinutes(5), dataRightsCase.DecidedAtUtc);
        Assert.Equal(
            "DataRights.TransitionInvalid",
            dataRightsCase.RecordDecision(
                DataRightsCaseDecision.Denied,
                DataRightsCaseDecisionReason.RequestInvalid,
                6,
                "user:other",
                Now.AddMinutes(6)).Error.Code);
    }

    [Fact]
    public void Decision_reason_must_match_the_outcome()
    {
        DataRightsCase dataRightsCase = CreateReviewRequired();
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(
            "DataRights.DecisionInvalid",
            dataRightsCase.RecordDecision(
                DataRightsCaseDecision.Approved,
                DataRightsCaseDecisionReason.LegalObligation,
                5,
                "user:decision-maker",
                Now.AddMinutes(5)).Error.Code);
        Assert.Equal(DataRightsCaseState.DecisionPending, dataRightsCase.Status);
        Assert.Equal(5, dataRightsCase.Version);
        Assert.Null(dataRightsCase.DecisionRevision);

        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Denied,
            DataRightsCaseDecisionReason.LegalObligation,
            5,
            "user:decision-maker",
            Now.AddMinutes(5)).IsSuccess);
        Assert.Equal(DataRightsCaseState.Denied, dataRightsCase.Status);
        Assert.Equal(6, dataRightsCase.DecisionRevision);
    }

    [Fact]
    public void Anonymisation_approval_requires_exact_immutable_policy_evidence()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateReviewRequired(
            propertyId,
            DataRightsCaseOperation.Anonymisation);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(
            "DataRights.ApprovalPolicyEvidenceInvalid",
            dataRightsCase.RecordDecision(
                DataRightsCaseDecision.Approved,
                DataRightsCaseDecisionReason.RequestValidated,
                5,
                "user:decision-maker",
                Now.AddMinutes(5)).Error.Code);

        DataRightsApprovalPolicyEvidence wrongScopeEvidence =
            DataRightsApprovalPolicyEvidence.Create(
                Guid.NewGuid(),
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
                Now.AddMinutes(5)).Value;
        Assert.Equal(
            "DataRights.ApprovalPolicyEvidenceInvalid",
            dataRightsCase.RecordDecision(
                DataRightsCaseDecision.Approved,
                DataRightsCaseDecisionReason.RequestValidated,
                5,
                "user:decision-maker",
                Now.AddMinutes(5),
                wrongScopeEvidence).Error.Code);

        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
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
                Now.AddMinutes(5)).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            Now.AddMinutes(5),
            evidence).IsSuccess);

        DataRightsApprovalPolicyEvidence persisted =
            Assert.IsType<DataRightsApprovalPolicyEvidence>(
                dataRightsCase.ApprovalPolicyEvidence);
        Assert.Same(evidence, persisted);
        Assert.True(persisted.RequiresDistinctExecutor);
    }

    [Fact]
    public void Anonymisation_cannot_share_an_approval_with_other_operations()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateReviewRequired(
            propertyId,
            DataRightsCaseOperation.Anonymisation | DataRightsCaseOperation.Correction);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            Now.AddMinutes(4)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
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
                Now.AddMinutes(5)).Value;

        Assert.Equal(
            "DataRights.ApprovalPolicyEvidenceInvalid",
            dataRightsCase.RecordDecision(
                DataRightsCaseDecision.Approved,
                DataRightsCaseDecisionReason.RequestValidated,
                5,
                "user:decision-maker",
                Now.AddMinutes(5),
                evidence).Error.Code);
    }

    [Fact]
    public void Approved_anonymisation_can_begin_once_with_a_distinct_executor()
    {
        DataRightsCase dataRightsCase = CreateApprovedAnonymisation();

        Assert.True(dataRightsCase.BeginAnonymisationExecution(
            6,
            " user:executor ",
            Now.AddMinutes(6)).IsSuccess);

        Assert.Equal(DataRightsCaseState.Executing, dataRightsCase.Status);
        Assert.Equal(7, dataRightsCase.ExecutionRevision);
        Assert.Equal(7, dataRightsCase.Version);
        Assert.Equal("user:executor", dataRightsCase.ExecutionStartedBy);
        Assert.Equal(Now.AddMinutes(6), dataRightsCase.ExecutionStartedAtUtc);
        Assert.Equal(
            "DataRights.TransitionInvalid",
            dataRightsCase.BeginAnonymisationExecution(
                7,
                "user:other-executor",
                Now.AddMinutes(7)).Error.Code);
    }

    [Fact]
    public void Anonymisation_decision_actor_cannot_execute_their_own_approval()
    {
        DataRightsCase dataRightsCase = CreateApprovedAnonymisation();

        Assert.Equal(
            "DataRights.DecisionActorCannotExecute",
            dataRightsCase.BeginAnonymisationExecution(
                6,
                "user:decision-maker",
                Now.AddMinutes(6)).Error.Code);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.ExecutionRevision);
        Assert.Null(dataRightsCase.ExecutionStartedBy);
    }

    [Theory]
    [InlineData(2, 0, 0, 0, DataRightsCaseState.Completed)]
    [InlineData(1, 0, 1, 0, DataRightsCaseState.PartiallyCompleted)]
    [InlineData(0, 0, 1, 1, DataRightsCaseState.Blocked)]
    public void Multi_owner_execution_reconciles_only_terminal_owner_outcomes(
        int completed,
        int noOp,
        int blocked,
        int failed,
        DataRightsCaseState expectedState)
    {
        DataRightsCase dataRightsCase = CreateApprovedMultiOwnerAnonymisation();
        Assert.True(dataRightsCase.BeginAnonymisationExecution(
            7,
            "user:executor",
            Now.AddMinutes(7)).IsSuccess);

        Assert.True(dataRightsCase.ReconcileAnonymisationExecution(
            dataRightsCase.Version,
            totalCount: 2,
            completed,
            noOp,
            blocked,
            failed,
            "system:data-rights-anonymisation",
            Now.AddMinutes(8)).IsSuccess);
        Assert.Equal(expectedState, dataRightsCase.Status);
        long terminalVersion = dataRightsCase.Version;
        Assert.True(dataRightsCase.ReconcileAnonymisationExecution(
            expectedVersion: 1,
            totalCount: 2,
            completed,
            noOp,
            blocked,
            failed,
            "system:data-rights-anonymisation",
            Now.AddMinutes(9)).IsSuccess);
        Assert.Equal(terminalVersion, dataRightsCase.Version);
    }

    [Fact]
    public void Execution_reconciliation_rejects_incomplete_owner_counts()
    {
        DataRightsCase dataRightsCase = CreateApprovedMultiOwnerAnonymisation();
        Assert.True(dataRightsCase.BeginAnonymisationExecution(
            7,
            "user:executor",
            Now.AddMinutes(7)).IsSuccess);

        Assert.Equal(
            "DataRights.ExecutionOutcomeInvalid",
            dataRightsCase.ReconcileAnonymisationExecution(
                dataRightsCase.Version,
                totalCount: 2,
                completedCount: 1,
                noOpCount: 0,
                blockedCount: 0,
                failedCount: 0,
                "system:data-rights-anonymisation",
                Now.AddMinutes(8)).Error.Code);
        Assert.Equal(DataRightsCaseState.Executing, dataRightsCase.Status);
    }

    [Fact]
    public void Case_changes_reject_timestamp_regression()
    {
        DataRightsCase dataRightsCase = CreateReviewRequired();

        Assert.Equal(
            "DataRights.TimestampInvalid",
            dataRightsCase.BeginDecision(
                4,
                "user:decision-maker",
                Now.AddMinutes(2)).Error.Code);
        Assert.Equal(DataRightsCaseState.ReviewRequired, dataRightsCase.Status);
        Assert.Equal(4, dataRightsCase.Version);
    }

    [Fact]
    public void Subject_selection_is_bounded_deduplicated_and_removable()
    {
        DataRightsCase dataRightsCase = Create(
            Guid.NewGuid(),
            DataRightsRequesterRelation.ControllerInitiated);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
        Guid firstRecordId = Guid.NewGuid();
        Assert.True(dataRightsCase.SelectSubject(
            " Guests ",
            " Guest-Profile ",
            firstRecordId,
            4,
            2,
            "user:operator-a",
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(
            "DataRights.SubjectAlreadySelected",
            dataRightsCase.SelectSubject(
                "guests",
                "guest-profile",
                firstRecordId,
                5,
                3,
                "user:operator-a",
                Now.AddMinutes(3)).Error.Code);
        Assert.True(dataRightsCase.UnselectSubject(
            "guests",
            "guest-profile",
            firstRecordId,
            3,
            "user:operator-a",
            Now.AddMinutes(3)).IsSuccess);
        Assert.Empty(dataRightsCase.SelectedSubjects);

        long version = dataRightsCase.Version;
        for (int index = 0; index < DataRightsCase.MaxSelectedSubjects; index++)
        {
            Assert.True(dataRightsCase.SelectSubject(
                "guests",
                "guest-profile",
                Guid.NewGuid(),
                1,
                version++,
                "user:operator-a",
                Now.AddMinutes(4)).IsSuccess);
        }

        Assert.Equal(
            "DataRights.SubjectSelectionLimitReached",
            dataRightsCase.SelectSubject(
                "guests",
                "guest-profile",
                Guid.NewGuid(),
                1,
                version,
                "user:operator-a",
                Now.AddMinutes(5)).Error.Code);
    }

    [Fact]
    public void Case_request_rejects_invalid_scope_and_operation_combinations()
    {
        Assert.Equal(
            "DataRights.PropertyRequired",
            DataRightsCaseRequest.Create(
                propertyId: null,
                DataRightsCaseKind.GuestRights,
                DataRightsCaseOperation.AccessExport,
                DataRightsRequesterRelation.DataSubject).Error.Code);
        Assert.Equal(
            "DataRights.PropertyNotAllowed",
            DataRightsCaseRequest.Create(
                Guid.NewGuid(),
                DataRightsCaseKind.TenantTermination,
                DataRightsCaseOperation.AccessExport,
                DataRightsRequesterRelation.TenantOwner).Error.Code);
        Assert.Equal(
            "DataRights.OperationsInvalid",
            DataRightsCaseRequest.Create(
                Guid.NewGuid(),
                DataRightsCaseKind.GuestRights,
                DataRightsCaseOperation.None,
                DataRightsRequesterRelation.DataSubject).Error.Code);
        Assert.Equal(
            "DataRights.OperationsInvalid",
            DataRightsCaseRequest.Create(
                Guid.NewGuid(),
                DataRightsCaseKind.GuestRights,
                DataRightsCaseOperation.AccessExport |
                    DataRightsCaseOperation.Restriction,
                DataRightsRequesterRelation.ControllerInitiated,
                DataRightsRestrictionAction.Apply).Error.Code);
        Assert.Equal(
            "DataRights.GuestRightsRequesterInvalid",
            DataRightsCaseRequest.Create(
                Guid.NewGuid(),
                DataRightsCaseKind.GuestRights,
                DataRightsCaseOperation.AccessExport,
                DataRightsRequesterRelation.TenantOwner).Error.Code);
        Assert.Equal(
            "DataRights.PropertyNotAllowed",
            DataRightsCaseRequest.Create(
                Guid.NewGuid(),
                DataRightsCaseKind.StaffRights,
                DataRightsCaseOperation.AccessExport,
                DataRightsRequesterRelation.DataSubject).Error.Code);
        Assert.Equal(
            "DataRights.StaffRightsOperationsInvalid",
            DataRightsCaseRequest.Create(
                propertyId: null,
                DataRightsCaseKind.StaffRights,
                DataRightsCaseOperation.AccessExport |
                    DataRightsCaseOperation.Correction,
                DataRightsRequesterRelation.DataSubject).Error.Code);
        Assert.Equal(
            "DataRights.StaffRightsRequesterInvalid",
            DataRightsCaseRequest.Create(
                propertyId: null,
                DataRightsCaseKind.StaffRights,
                DataRightsCaseOperation.AccessExport,
                DataRightsRequesterRelation.TenantOwner).Error.Code);
    }

    [Theory]
    [InlineData(
        DataRightsRequesterRelation.DataSubject,
        DataRightsCaseOperation.AccessExport,
        DataRightsRestrictionAction.None)]
    [InlineData(
        DataRightsRequesterRelation.AuthorizedRepresentative,
        DataRightsCaseOperation.Correction,
        DataRightsRestrictionAction.None)]
    [InlineData(
        DataRightsRequesterRelation.ControllerInitiated,
        DataRightsCaseOperation.Restriction,
        DataRightsRestrictionAction.Apply)]
    [InlineData(
        DataRightsRequesterRelation.ControllerInitiated,
        DataRightsCaseOperation.Anonymisation,
        DataRightsRestrictionAction.None)]
    public void Staff_rights_case_request_accepts_one_supported_tenant_operation(
        DataRightsRequesterRelation requesterRelationship,
        DataRightsCaseOperation operation,
        DataRightsRestrictionAction restrictionAction)
    {
        Result<DataRightsCaseRequest> result = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            operation,
            requesterRelationship,
            restrictionAction);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PropertyId);
        Assert.Equal(DataRightsCaseKind.StaffRights, result.Value.Kind);
        Assert.Equal(operation, result.Value.RequestedOperations);
        Assert.Equal(requesterRelationship, result.Value.RequesterRelationship);
    }

    [Theory]
    [InlineData(DataRightsCaseOperation.Restriction, DataRightsRestrictionAction.None, false)]
    [InlineData(DataRightsCaseOperation.Restriction, DataRightsRestrictionAction.Apply, true)]
    [InlineData(DataRightsCaseOperation.Restriction, DataRightsRestrictionAction.Release, true)]
    [InlineData(DataRightsCaseOperation.AccessExport, DataRightsRestrictionAction.Apply, false)]
    [InlineData(DataRightsCaseOperation.AccessExport, DataRightsRestrictionAction.Release, false)]
    public void Case_request_requires_a_directive_only_for_restriction(
        DataRightsCaseOperation operations,
        DataRightsRestrictionAction restrictionAction,
        bool expectedSuccess)
    {
        Result<DataRightsCaseRequest> result = DataRightsCaseRequest.Create(
            Guid.NewGuid(),
            DataRightsCaseKind.GuestRights,
            operations,
            DataRightsRequesterRelation.ControllerInitiated,
            restrictionAction);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (expectedSuccess)
        {
            Assert.Equal(restrictionAction, result.Value.RestrictionAction);
        }
        else
        {
            Assert.Equal("DataRights.RestrictionDirectiveInvalid", result.Error.Code);
        }
    }

    private static DataRightsCase Create(
        Guid propertyId,
        DataRightsRequesterRelation requesterRelationship,
        DataRightsCaseOperation operations = DataRightsCaseOperation.AccessExport)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            operations,
            requesterRelationship).Value;
        return DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "  user:operator-a  ",
            Now).Value;
    }

    private static DataRightsResponseDeadlinePolicyEvidence CreateDeadlineEvidence(
        Guid propertyId,
        DateTimeOffset evaluatedAtUtc) =>
        DataRightsResponseDeadlinePolicyEvidence.Create(
            propertyId,
            7,
            8,
            "GB",
            "development-hostel-example",
            2,
            new string('a', 64),
            DataRightsResponseRight.Export,
            "development-example",
            0,
            1,
            0,
            "Europe/London",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Now,
            evaluatedAtUtc,
            Now.AddMonths(1)).Value;

    private static DataRightsCase CreateReviewRequired()
        => CreateReviewRequired(
            Guid.NewGuid(),
            DataRightsCaseOperation.AccessExport | DataRightsCaseOperation.Correction);

    private static DataRightsCase CreateReviewRequired(
        Guid propertyId,
        DataRightsCaseOperation operations)
    {
        DataRightsCase dataRightsCase = Create(
            propertyId,
            DataRightsRequesterRelation.ControllerInitiated,
            operations);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            1,
            2,
            "user:operator-a",
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator-a",
            Now.AddMinutes(3)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateApprovedAnonymisation()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateReviewRequired(
            propertyId,
            DataRightsCaseOperation.Anonymisation);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            Now.AddMinutes(4)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
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
                Now.AddMinutes(5)).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            Now.AddMinutes(5),
            evidence).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateApprovedMultiOwnerAnonymisation()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = Create(
            propertyId,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsCaseOperation.Anonymisation);
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator-a",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            1,
            2,
            "user:operator-a",
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "reservations",
            "reservation",
            Guid.NewGuid(),
            2,
            3,
            "user:operator-a",
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            4,
            "user:operator-a",
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            5,
            "user:decision-maker",
            Now.AddMinutes(5)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
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
                Now.AddMinutes(6)).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            6,
            "user:decision-maker",
            Now.AddMinutes(6),
            evidence).IsSuccess);
        return dataRightsCase;
    }
}
