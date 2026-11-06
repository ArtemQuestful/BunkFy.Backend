namespace BunkFy.Modules.DataRights.Tests.Domain;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsCorrectionExecutionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Correction_requires_exactly_one_selected_subject()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        SelectSubject(dataRightsCase, Guid.NewGuid(), "guests", "guest-profile");
        SelectSubject(dataRightsCase, Guid.NewGuid(), "reservations", "reservation");

        var result = dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4));

        Assert.Equal("DataRights.CorrectionExecutionInvalid", result.Error.Code);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
    }

    [Fact]
    public void Claim_is_exclusive_while_active_and_transferable_only_after_expiry()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        long selectedCaseVersion = dataRightsCase.Version;
        Assert.True(dataRightsCase.BeginCorrectionExecution(
            selectedCaseVersion,
            "user:executor",
            Now).IsSuccess);
        DataRightsSubjectCoordinate subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionExecution execution = DataRightsCorrectionExecution.Create(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            dataRightsCase.Kind,
            dataRightsCase.PropertyId!.Value,
            dataRightsCase.Id,
            selectedCaseVersion,
            dataRightsCase.ExecutionRevision!.Value,
            dataRightsCase.DecisionRevision!.Value,
            subject,
            "guests.guest-profile.correction.v1",
            "user:executor",
            Now,
            Now.AddMinutes(10)).Value;

        Assert.True(execution.MatchesAuthorization(
            dataRightsCase.Kind,
            dataRightsCase.PropertyId.Value,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision.Value,
            execution.Id,
            " GUESTS ",
            "GUEST-PROFILE",
            subject.RecordId,
            subject.RecordVersion,
            "GUESTS.GUEST-PROFILE.CORRECTION.V1",
            "user:executor"));
        Assert.False(execution.MatchesAuthorization(
            dataRightsCase.Kind,
            dataRightsCase.PropertyId.Value,
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision.Value,
            execution.Id,
            subject.OwnerKey,
            subject.RecordType,
            subject.RecordId,
            subject.RecordVersion,
            execution.FieldPolicyKey,
            "user:other"));

        Assert.True(execution.Renew(
            "user:executor",
            Now.AddMinutes(5),
            Now.AddMinutes(15)).IsSuccess);
        Assert.Equal(Now, execution.StartedAtUtc);
        Assert.Equal(1, execution.Version);
        Assert.True(execution.Renew(
            "user:other",
            Now.AddMinutes(11),
            Now.AddMinutes(21)).IsSuccess);
        Assert.Equal(Now.AddMinutes(11), execution.StartedAtUtc);
        Assert.Equal("user:other", execution.ExecutedBy);
        Assert.Equal(2, execution.Version);
        Assert.Equal(
            "DataRights.CorrectionExecutionConflict",
            execution.Renew(
                "user:executor",
                Now.AddMinutes(12),
                Now.AddMinutes(22)).Error.Code);
    }

    [Fact]
    public void Delayed_authorized_completion_survives_expiry_and_takeover()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        long selectedCaseVersion = dataRightsCase.Version;
        Assert.True(dataRightsCase.BeginCorrectionExecution(
            selectedCaseVersion,
            "user:executor",
            Now).IsSuccess);
        DataRightsSubjectCoordinate subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionExecution execution = DataRightsCorrectionExecution.Create(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            dataRightsCase.Kind,
            dataRightsCase.PropertyId!.Value,
            dataRightsCase.Id,
            selectedCaseVersion,
            dataRightsCase.ExecutionRevision!.Value,
            dataRightsCase.DecisionRevision!.Value,
            subject,
            "guests.guest-profile.correction.v1",
            "user:executor",
            Now,
            Now.AddMinutes(10)).Value;
        DateTimeOffset ownerCommittedAt = Now.AddMinutes(10).AddSeconds(1);

        Assert.True(execution.Renew(
            "user:other",
            Now.AddMinutes(11),
            Now.AddMinutes(21)).IsSuccess);
        var completed = execution.Complete(
            execution.Version,
            receiptContractVersion: 1,
            Guid.NewGuid(),
            subject.RecordVersion + 1,
            changedFieldCount: 1,
            new string('a', 64),
            new string('b', 64),
            ownerCommittedAt);

        Assert.True(completed.IsSuccess, completed.Error.Code);
        Assert.Equal(ownerCommittedAt, execution.CompletedAtUtc);
    }

    [Fact]
    public void Completion_is_exact_idempotent_and_preserves_pii_free_proof()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase();
        long selectedCaseVersion = dataRightsCase.Version;
        Assert.True(dataRightsCase.BeginCorrectionExecution(
            selectedCaseVersion,
            "user:executor",
            Now).IsSuccess);
        DataRightsSubjectCoordinate subject = dataRightsCase.SelectedSubjects.Single();
        DataRightsCorrectionExecution execution = DataRightsCorrectionExecution.Create(
            Guid.NewGuid(),
            dataRightsCase.ScopeId,
            dataRightsCase.Kind,
            dataRightsCase.PropertyId!.Value,
            dataRightsCase.Id,
            selectedCaseVersion,
            dataRightsCase.ExecutionRevision!.Value,
            dataRightsCase.DecisionRevision!.Value,
            subject,
            "guests.guest-profile.correction.v1",
            "user:executor",
            Now,
            Now.AddMinutes(10)).Value;
        Guid receiptId = Guid.NewGuid();
        DateTimeOffset completedAt = Now.AddMinutes(1);

        var completed = execution.Complete(
            execution.Version,
            receiptContractVersion: 1,
            receiptId,
            subject.RecordVersion + 1,
            changedFieldCount: 2,
            new string('a', 64),
            new string('b', 64),
            completedAt);
        var replay = execution.Complete(
            expectedVersion: 1,
            receiptContractVersion: 1,
            receiptId,
            subject.RecordVersion + 1,
            changedFieldCount: 2,
            new string('a', 64),
            new string('b', 64),
            completedAt);
        var changedReplay = execution.Complete(
            execution.Version,
            receiptContractVersion: 1,
            receiptId,
            subject.RecordVersion + 1,
            changedFieldCount: 1,
            new string('a', 64),
            new string('b', 64),
            completedAt);

        Assert.True(completed.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            "DataRights.CorrectionExecutionConflict",
            changedReplay.Error.Code);
        Assert.Equal(DataRightsCorrectionExecutionState.Completed, execution.State);
        Assert.Equal(subject.RecordVersion + 1, execution.CurrentRecordVersion);
        Assert.Equal(2, execution.ChangedFieldCount);
        Assert.DoesNotContain(
            typeof(DataRightsCorrectionExecution).GetProperties(),
            property => property.Name.Contains("Value", StringComparison.Ordinal));
    }

    private static DataRightsCase CreateApprovedCase()
    {
        DataRightsCase dataRightsCase = CreateDiscoveryCase();
        SelectSubject(dataRightsCase, Guid.NewGuid(), "guests", "guest-profile");
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-3)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-2)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateDiscoveryCase()
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
            Now.AddMinutes(-7)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-6)).IsSuccess);
        return dataRightsCase;
    }

    private static void SelectSubject(
        DataRightsCase dataRightsCase,
        Guid recordId,
        string ownerKey,
        string recordType) =>
        Assert.True(dataRightsCase.SelectSubject(
            ownerKey,
            recordType,
            recordId,
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-5)).IsSuccess);
}
