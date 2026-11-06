namespace BunkFy.Modules.Retention.Tests.Domain;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Errors;
using BunkFy.Modules.Retention.Domain.Events;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionRunRetryRequestTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_pins_evidence_and_emits_a_payload_free_request_event()
    {
        Guid requestId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();

        Result<RetentionRunRetryRequest> result =
            RetentionRunRetryRequest.Create(
                requestId,
                eventId,
                "tenant-a",
                runId,
                "guests",
                "guest-operational",
                RetentionExecutionTargetKind.Property,
                propertyId,
                executionPolicyVersion: 2,
                evidenceVersion: 9,
                Now,
                scheduledAtUtc: null);

        Assert.True(result.IsSuccess);
        RetentionRunRetryRequest request = result.Value;
        Assert.Equal(RetentionRunRetryRequestState.Pending, request.State);
        Assert.Equal(1, request.Attempt);
        Assert.Equal(9, request.EvidenceVersion);
        RetentionRunRetryRequestedDomainEvent raised =
            Assert.IsType<RetentionRunRetryRequestedDomainEvent>(
                Assert.Single(request.DomainEvents));
        Assert.Equal(eventId, raised.EventId);
        Assert.Equal(requestId, raised.RequestId);
        Assert.Equal(runId, raised.RunId);
        Assert.Equal(1, raised.Attempt);
    }

    [Fact]
    public void Failed_request_can_be_requeued_without_changing_its_evidence()
    {
        RetentionRunRetryRequest request = Create();
        Assert.True(request.MarkFailed(
            "task-run-unavailable",
            Now.AddMinutes(1)).IsSuccess);

        Result result = request.RequestAgain(
            Guid.NewGuid(),
            Now.AddMinutes(2),
            scheduledAtUtc: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(RetentionRunRetryRequestState.Pending, request.State);
        Assert.Equal(2, request.Attempt);
        Assert.Equal(7, request.EvidenceVersion);
        Assert.Null(request.CompletedAtUtc);
        Assert.Null(request.FailureCode);
        Assert.Equal(
            2,
            request.DomainEvents
                .OfType<RetentionRunRetryRequestedDomainEvent>()
                .Last()
                .Attempt);
    }

    [Fact]
    public void Applied_request_is_idempotent_and_cannot_be_requeued()
    {
        RetentionRunRetryRequest request = Create();
        DateTimeOffset completed = Now.AddMinutes(1);

        Assert.True(request.MarkApplied(completed).IsSuccess);
        Assert.True(request.MarkApplied(completed.AddMinutes(1)).IsSuccess);
        Result requeue = request.RequestAgain(
            Guid.NewGuid(),
            completed.AddMinutes(2),
            scheduledAtUtc: null);

        Assert.True(requeue.IsFailure);
        Assert.Equal(
            RetentionDomainErrors.RecoveryTransitionInvalid,
            requeue.Error);
        Assert.Equal(completed, request.CompletedAtUtc);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Invalid_policy_or_evidence_version_is_rejected(
        int policyVersion,
        long evidenceVersion)
    {
        Result<RetentionRunRetryRequest> result =
            RetentionRunRetryRequest.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                "guests",
                "guest-operational",
                RetentionExecutionTargetKind.Tenant,
                propertyId: null,
                policyVersion,
                evidenceVersion,
                Now,
                scheduledAtUtc: null);

        Assert.True(result.IsFailure);
        Assert.Equal(RetentionDomainErrors.RecoveryRequestInvalid, result.Error);
    }

    private static RetentionRunRetryRequest Create() =>
        RetentionRunRetryRequest.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "guests",
            "guest-operational",
            RetentionExecutionTargetKind.Tenant,
            propertyId: null,
            executionPolicyVersion: 2,
            evidenceVersion: 7,
            Now,
            scheduledAtUtc: null).Value;
}
