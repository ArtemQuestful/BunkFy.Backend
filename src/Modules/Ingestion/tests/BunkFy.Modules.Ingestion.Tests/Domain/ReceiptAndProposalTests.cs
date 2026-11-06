namespace BunkFy.Modules.Ingestion.Tests.Domain;

using BunkFy.Modules.Ingestion.Domain.Errors;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReceiptAndProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Receipt_has_stable_dedup_identity_and_one_terminal_outcome()
    {
        ObservationReceipt receipt = CreateReceipt();

        Assert.Equal("reservation.changed", receipt.SourceRecordType);
        Assert.True(receipt.MarkProcessed(Now.AddSeconds(1)).IsSuccess);
        Assert.Equal(ObservationReceiptState.Processed, receipt.State);
        Assert.Equal(
            IngestionDomainErrors.ReceiptNotPending,
            receipt.Reject("invalid", Now.AddSeconds(2)).Error);
    }

    [Fact]
    public void Push_receipt_does_not_require_a_worker_run()
    {
        ObservationReceipt receipt = CreateReceipt(runId: null);

        Assert.Null(receipt.RunId);
        Assert.Equal(ObservationReceiptState.Pending, receipt.State);
    }

    [Fact]
    public void Staff_can_reject_a_pending_proposal_with_a_reason()
    {
        ChangeProposal proposal = CreateProposal();

        Assert.True(proposal.Reject(
            "staff:42", "Already corrected locally", 1, Now.AddDays(90), Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(ChangeProposalState.Rejected, proposal.State);
        Assert.Equal("staff:42", proposal.DecisionActor);
        Assert.Equal("Already corrected locally", proposal.DecisionReason);
        Assert.Equal(Now.AddDays(90), proposal.SensitiveDataRetainUntilUtc);
    }

    [Fact]
    public void Applying_proposal_can_become_stale_when_reservation_revision_races()
    {
        ChangeProposal proposal = CreateProposal();

        Guid operationId = Guid.NewGuid();
        Assert.True(proposal.BeginApply("staff:42", operationId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(proposal.MarkStale(
            "Reservation details changed during apply", 2, Now.AddDays(90), Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(ChangeProposalState.Stale, proposal.State);
        Assert.Equal(operationId, proposal.ProductOperationId);
    }

    [Fact]
    public void Proposal_redaction_preserves_non_sensitive_audit_and_is_idempotent()
    {
        ChangeProposal proposal = CreateProposal();
        Assert.Equal(
            IngestionDomainErrors.SensitiveHistoryNotRedactable,
            proposal.RedactSensitiveData(Now.AddDays(100)).Error);
        Assert.True(proposal.Reject(
            "staff:42", "Outdated", proposal.Version, Now.AddDays(90), Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(
            IngestionDomainErrors.SensitiveHistoryNotRedactable,
            proposal.RedactSensitiveData(Now.AddDays(89)).Error);

        Assert.True(proposal.RedactSensitiveData(Now.AddDays(90)).IsSuccess);
        long redactedVersion = proposal.Version;

        Assert.Null(proposal.Diff);
        Assert.Equal("staff-conflict", proposal.ReasonCode);
        Assert.Equal("staff:42", proposal.DecisionActor);
        Assert.Equal("Outdated", proposal.DecisionReason);
        Assert.Equal(Now.AddDays(90), proposal.SensitiveDataRedactedAtUtc);
        Assert.True(proposal.RedactSensitiveData(Now.AddDays(91)).IsSuccess);
        Assert.Equal(redactedVersion, proposal.Version);
    }

    [Fact]
    public void Accepted_cancellation_dispatch_remains_sensitive_until_final_fact_then_redacts()
    {
        ReservationDispatch dispatch = CreateDispatch(ReservationDispatchKind.Cancel);
        Assert.True(dispatch.Complete(
            ReservationDispatchState.Accepted,
            dispatch.ReservationId,
            detailsRevision: 2,
            reservationVersion: 3,
            errorCode: null,
            sensitiveDataRetainUntilUtc: null,
            Now.AddMinutes(1)).IsSuccess);
        Assert.Null(dispatch.SensitiveDataRetainUntilUtc);
        Assert.Equal(
            IngestionDomainErrors.SensitiveHistoryNotRedactable,
            dispatch.RedactSensitiveData(Now.AddYears(1)).Error);

        Assert.True(dispatch.ConfirmAcceptedCancellation(4, Now.AddDays(90), Now.AddMinutes(2)).IsSuccess);
        Assert.True(dispatch.RedactSensitiveData(Now.AddDays(90)).IsSuccess);
        Assert.Null(dispatch.NormalizedSnapshot);
        Assert.Equal(ReservationDispatchKind.Cancel, dispatch.Kind);
        Assert.Equal(ReservationDispatchState.Applied, dispatch.State);
    }

    [Fact]
    public void Proposal_transitions_are_optimistically_versioned()
    {
        ChangeProposal proposal = CreateProposal();

        Assert.Equal(
            IngestionDomainErrors.VersionConflict,
            proposal.BeginApply("staff:42", Guid.NewGuid(), 2, Now).Error);
        Assert.Equal(ChangeProposalState.Pending, proposal.State);
    }

    [Fact]
    public void Dispatch_coordinates_follow_operation_kind_and_cannot_rebind_a_reservation()
    {
        Guid reservationId = Guid.NewGuid();
        var createWithReservation = ReservationDispatch.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(),
            ReservationDispatchTriggerKind.Observation, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), reservationId, ReservationDispatchKind.Create,
            "1", 1, "{\"guest\":\"Sensitive\"}", null, Now);
        var updateWithoutReservation = ReservationDispatch.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(),
            ReservationDispatchTriggerKind.Observation, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), null, ReservationDispatchKind.Amend,
            "1", 1, "{\"guest\":\"Sensitive\"}", 1, Now);

        Assert.Equal(IngestionDomainErrors.ReservationDispatchInvalid, createWithReservation.Error);
        Assert.Equal(IngestionDomainErrors.ReservationDispatchInvalid, updateWithoutReservation.Error);

        ReservationDispatch dispatch = CreateDispatch(ReservationDispatchKind.Amend);
        Assert.Equal(
            IngestionDomainErrors.ReservationDispatchInvalid,
            dispatch.Complete(
                ReservationDispatchState.Applied,
                Guid.NewGuid(),
                2,
                3,
                null,
                Now.AddDays(90),
                Now.AddMinutes(1)).Error);
        Assert.Equal(
            IngestionDomainErrors.ReservationDispatchInvalid,
            dispatch.Complete(
                ReservationDispatchState.Applied,
                dispatch.ReservationId,
                2,
                3,
                "unexpected-error",
                Now.AddDays(90),
                Now.AddMinutes(1)).Error);
        Assert.Equal(ReservationDispatchState.Pending, dispatch.State);
    }

    [Fact]
    public void Source_link_rejects_incoherent_cancellation_and_reservation_rebinding()
    {
        ReservationSourceLink link = ReservationSourceLink.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "fake.http",
            "booking-123",
            Now).Value;
        Guid receiptId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        Assert.True(link.Observe(
            receiptId,
            "1",
            1,
            Now,
            new string('a', ReservationSourceLink.ContentHashLength),
            Now).IsSuccess);
        Assert.True(link.BeginDispatch(operationId, Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(
            IngestionDomainErrors.ReservationObservationInvalid,
            link.CompleteDispatch(
                operationId,
                receiptId,
                "1",
                1,
                operationalBaseline: null,
                reservationId,
                detailsRevision: 1,
                keepActive: false,
                applied: true,
                cancellationPending: true,
                cancelled: false,
                Now.AddMinutes(2)).Error);
        Assert.True(link.CompleteDispatch(
            operationId,
            receiptId,
            "1",
            1,
            "{\"schemaVersion\":1}",
            reservationId,
            detailsRevision: 1,
            keepActive: false,
            applied: true,
            cancellationPending: false,
            cancelled: false,
            Now.AddMinutes(2)).IsSuccess);

        Guid nextReceiptId = Guid.NewGuid();
        Guid nextOperationId = Guid.NewGuid();
        Assert.True(link.Observe(
            nextReceiptId,
            "2",
            2,
            Now.AddMinutes(3),
            new string('b', ReservationSourceLink.ContentHashLength),
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(link.BeginDispatch(nextOperationId, Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(
            IngestionDomainErrors.ReservationObservationInvalid,
            link.CompleteDispatch(
                nextOperationId,
                nextReceiptId,
                "2",
                2,
                "{\"schemaVersion\":1}",
                Guid.NewGuid(),
                detailsRevision: 2,
                keepActive: false,
                applied: true,
                cancellationPending: false,
                cancelled: false,
                Now.AddMinutes(5)).Error);
        Assert.Equal(reservationId, link.ReservationId);
    }

    [Fact]
    public void Terminal_source_history_can_be_anonymised_before_automatic_retention()
    {
        ChangeProposal proposal = CreateProposal();
        Assert.True(proposal.Reject(
            "staff:42",
            "Outdated",
            proposal.Version,
            Now.AddDays(90),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(proposal.Anonymise(Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(Guid.Empty, proposal.ReservationId);
        Assert.Null(proposal.Diff);
        Assert.Null(proposal.DecisionReason);

        ReservationDispatch dispatch = CreateDispatch(ReservationDispatchKind.Amend);
        Assert.True(dispatch.Complete(
            ReservationDispatchState.Applied,
            dispatch.ReservationId,
            2,
            3,
            null,
            Now.AddDays(90),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dispatch.Anonymise(Now.AddMinutes(2)).IsSuccess);
        Assert.Null(dispatch.ReservationId);
        Assert.Null(dispatch.SourceRevision);
        Assert.Null(dispatch.NormalizedSnapshot);
    }

    [Fact]
    public void Raw_payload_purge_requires_terminal_due_receipt_and_matching_claim()
    {
        ObservationReceipt receipt = CreateReceipt();
        Guid claimId = Guid.NewGuid();
        DateTimeOffset purgeAt = Now.AddDays(31);

        Assert.Equal(
            IngestionDomainErrors.RawPayloadNotPurgeable,
            receipt.BeginRawPayloadPurge(claimId, purgeAt, purgeAt.AddMinutes(-15)).Error);
        Assert.True(receipt.MarkProcessed(Now.AddMinutes(1)).IsSuccess);
        Assert.True(receipt.BeginRawPayloadPurge(claimId, purgeAt, purgeAt.AddMinutes(-15)).IsSuccess);
        long claimedVersion = receipt.RawPayloadVersion;
        Assert.True(receipt.BeginRawPayloadPurge(claimId, purgeAt, purgeAt.AddMinutes(-15)).IsSuccess);
        Assert.Equal(claimedVersion, receipt.RawPayloadVersion);
        Assert.Equal(
            IngestionDomainErrors.RawPayloadPurgeAlreadyClaimed,
            receipt.BeginRawPayloadPurge(Guid.NewGuid(), purgeAt, purgeAt.AddMinutes(-15)).Error);
        Assert.Equal(
            IngestionDomainErrors.RawPayloadPurgeClaimInvalid,
            receipt.CompleteRawPayloadPurge(Guid.NewGuid(), purgeAt).Error);

        Assert.True(receipt.CompleteRawPayloadPurge(claimId, purgeAt).IsSuccess);
        Assert.Equal(RawPayloadRetentionState.Purged, receipt.RawPayloadRetentionState);
        Assert.Equal(purgeAt, receipt.RawPayloadPurgedAtUtc);
        Assert.Null(receipt.RawPayloadPurgeClaimId);
    }

    [Fact]
    public void Stale_raw_payload_claim_can_be_recovered_by_another_task()
    {
        ObservationReceipt receipt = CreateReceipt();
        Assert.True(receipt.MarkProcessed(Now.AddMinutes(1)).IsSuccess);
        Guid firstClaim = Guid.NewGuid();
        Guid recoveryClaim = Guid.NewGuid();
        DateTimeOffset firstPurgeAt = Now.AddDays(31);
        Assert.True(receipt.BeginRawPayloadPurge(
            firstClaim, firstPurgeAt, firstPurgeAt.AddMinutes(-15)).IsSuccess);

        DateTimeOffset recoveryAt = firstPurgeAt.AddMinutes(16);
        Assert.True(receipt.BeginRawPayloadPurge(
            recoveryClaim, recoveryAt, recoveryAt.AddMinutes(-15)).IsSuccess);
        Assert.Equal(recoveryClaim, receipt.RawPayloadPurgeClaimId);
        Assert.Equal(recoveryAt, receipt.RawPayloadPurgeStartedAtUtc);
    }

    private static ObservationReceipt CreateReceipt(Guid? runId = null) => ObservationReceipt.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        Guid.NewGuid(),
        runId,
        Guid.NewGuid(),
        " Reservation.Changed ",
        "booking-123",
        "7",
        "reservation.changed|booking-123|7",
        new string('a', ObservationReceipt.ContentHashLength),
        TestObservationCountryPolicyEvidence.Create(Now),
        Guid.NewGuid(),
        Now.AddDays(30),
        Now.AddMinutes(-2),
        Now.AddMinutes(-1),
        Now).Value;

    private static ChangeProposal CreateProposal() => ChangeProposal.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        3,
        "staff-conflict",
        "{\"arrival\":{\"from\":\"2026-08-01\",\"to\":\"2026-08-02\"}}",
        Now).Value;

    private static ReservationDispatch CreateDispatch(ReservationDispatchKind kind) => ReservationDispatch.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        ReservationDispatchTriggerKind.Observation,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        kind == ReservationDispatchKind.Create ? null : Guid.NewGuid(),
        kind,
        "1",
        1,
        "{\"guest\":\"Sensitive\"}",
        kind == ReservationDispatchKind.Create ? null : 1,
        Now).Value;
}
