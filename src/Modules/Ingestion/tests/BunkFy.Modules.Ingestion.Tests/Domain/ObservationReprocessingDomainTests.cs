namespace BunkFy.Modules.Ingestion.Tests.Domain;

using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.Ingestion.Domain.Errors;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ObservationReprocessingDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Active_reprocessing_reservation_fences_raw_purge_but_expired_reservation_does_not()
    {
        ObservationReceipt receipt = RejectedReceipt();
        Guid attemptId = Guid.NewGuid();

        Assert.True(receipt.ReserveForReprocessing(attemptId, Now.AddHours(1), Now).IsSuccess);
        Assert.Equal(IngestionDomainErrors.ReprocessingReservationActive,
            receipt.BeginRawPayloadPurge(Guid.NewGuid(), Now.AddMinutes(30), Now).Error);

        Guid purgeClaim = Guid.NewGuid();
        Assert.True(receipt.BeginRawPayloadPurge(
            purgeClaim,
            Now.AddHours(2),
            Now.AddHours(1)).IsSuccess);
        Assert.Null(receipt.ActiveReprocessingAttemptId);
        Assert.Null(receipt.ReprocessingReservationExpiresAtUtc);
    }

    [Fact]
    public void Receipt_requires_complete_derived_lineage_and_preserves_source_state()
    {
        ObservationReceipt source = RejectedReceipt();
        Guid attemptId = Guid.NewGuid();
        byte[] payload = "{}"u8.ToArray();

        var invalid = ObservationReceipt.Create(
            Guid.NewGuid(), "tenant-a", source.PropertyId, source.ConnectionId, null, Guid.NewGuid(),
            "reservation.v1", "booking-42", "1", "dedupe", AdapterPayloadHash.ComputeSha256(payload),
            TestObservationCountryPolicyEvidence.Create(Now),
            Guid.NewGuid(), Now.AddDays(30), null, Now, Now,
            sourceReceiptId: source.Id);
        Assert.Equal(IngestionDomainErrors.ReprocessingIdentityInvalid, invalid.Error);

        var derived = ObservationReceipt.Create(
            Guid.NewGuid(), "tenant-a", source.PropertyId, source.ConnectionId, null, Guid.NewGuid(),
            "reservation.v1", "booking-42", "1", "dedupe", AdapterPayloadHash.ComputeSha256(payload),
            TestObservationCountryPolicyEvidence.Create(Now),
            Guid.NewGuid(), Now.AddDays(30), null, Now, Now,
            source.Id, attemptId, "mail.reservation-json", 1, 0);

        Assert.True(derived.IsSuccess);
        Assert.Equal(source.Id, derived.Value.SourceReceiptId);
        Assert.Equal(attemptId, derived.Value.ReprocessingAttemptId);
        Assert.Equal(ObservationReceiptState.Rejected, source.State);
    }

    [Fact]
    public void Attempt_tracks_retry_and_terminal_counts_with_versioned_parser_identity()
    {
        Guid attemptId = Guid.NewGuid();
        var created = ObservationReprocessingAttempt.Create(
            attemptId, "tenant-a", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), attemptId,
            "mail.reservation-json", 2, "operator-1", Now, Now.AddHours(24));
        ObservationReprocessingAttempt attempt = created.Value;

        Assert.True(attempt.Start(attemptId, 1, Now.AddMinutes(1), Now.AddHours(2)).IsSuccess);
        Assert.True(attempt.ScheduleRetry(
            1, "parser.transient", Now.AddMinutes(2), Now.AddHours(24)).IsSuccess);
        Assert.True(attempt.Start(attemptId, 2, Now.AddMinutes(3), Now.AddHours(2)).IsSuccess);
        Assert.True(attempt.Complete(2, 1, 1, 0, false, null, Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(ObservationReprocessingState.Succeeded, attempt.State);
        Assert.Equal(2, attempt.LastTaskAttempt);
        Assert.Equal(2, attempt.ParsedCount);
        Assert.Equal(1, attempt.AcceptedCount);
        Assert.Equal(1, attempt.DuplicateCount);
        Assert.NotNull(attempt.CompletedAtUtc);
    }

    [Fact]
    public void Attempt_rejects_backwards_transitions_and_success_error_evidence()
    {
        Guid attemptId = Guid.NewGuid();
        ObservationReprocessingAttempt attempt = ObservationReprocessingAttempt.Create(
            attemptId,
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            attemptId,
            "mail.reservation-json",
            2,
            "operator-1",
            Now,
            Now.AddHours(24)).Value;

        Assert.Equal(
            IngestionDomainErrors.ReprocessingTaskInvalid,
            attempt.Start(attemptId, 1, Now.AddMinutes(-1), Now.AddHours(2)).Error);
        Assert.True(attempt.Start(attemptId, 1, Now.AddMinutes(1), Now.AddHours(2)).IsSuccess);
        Assert.Equal(
            IngestionDomainErrors.ReprocessingOutcomeInvalid,
            attempt.Complete(1, 1, 0, 0, false, null, Now).Error);
        Assert.Equal(
            IngestionDomainErrors.ReprocessingErrorInvalid,
            attempt.Complete(1, 1, 0, 0, false, "unexpected", Now.AddMinutes(2)).Error);
        Assert.True(attempt.Complete(1, 1, 0, 0, false, null, Now.AddMinutes(2)).IsSuccess);
        Assert.Null(attempt.LastErrorCode);
    }

    [Fact]
    public void Output_ledger_requires_receipt_for_success_and_error_for_rejection()
    {
        string hash = new('a', AdapterProtocolLimits.Sha256Length);
        Assert.Equal(IngestionDomainErrors.ReprocessingOutcomeInvalid,
            ObservationReprocessingOutput.Create(
                Guid.NewGuid(), "tenant-a", Guid.NewGuid(), 0, null,
                ObservationReprocessingOutputDisposition.Accepted,
                "reservation.v1", "booking-1", "1", hash, null, Now).Error);

        var rejected = ObservationReprocessingOutput.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(), 0, null,
            ObservationReprocessingOutputDisposition.Rejected,
            "reservation.v1", "booking-1", "1", hash, "ingestion.source-conflict", Now);
        Assert.True(rejected.IsSuccess);
    }

    private static ObservationReceipt RejectedReceipt()
    {
        byte[] payload = "raw"u8.ToArray();
        ObservationReceipt receipt = ObservationReceipt.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            "mail.unparsed.v1",
            "mailbox:42:7",
            "7",
            Guid.NewGuid().ToString("N"),
            AdapterPayloadHash.ComputeSha256(payload),
            TestObservationCountryPolicyEvidence.Create(Now),
            Guid.NewGuid(),
            Now.AddMinutes(5),
            null,
            Now,
            Now).Value;
        Assert.True(receipt.Reject("unsupported", Now).IsSuccess);
        return receipt;
    }
}
