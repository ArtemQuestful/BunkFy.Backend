namespace BunkFy.Modules.Ingestion.Domain.Reservations;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Ingestion.Domain.Errors;

public sealed class ReservationDispatch : ScopedAggregateRoot<Guid>
{
    public const int ErrorCodeMaxLength = 200;
    public const int SourceRevisionMaxLength = 256;
    public const int NormalizedSnapshotMaxLength = 16_384;

    private ReservationDispatch() { }

    private ReservationDispatch(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid SourceLinkId { get; private set; }
    public ReservationDispatchTriggerKind TriggerKind { get; private set; }
    public Guid TriggerId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid? ReservationId { get; private set; }
    public ReservationDispatchKind Kind { get; private set; }
    public string? SourceRevision { get; private set; }
    public long? SourceSequence { get; private set; }
    public string? NormalizedSnapshot { get; private set; }
    public long? ExpectedDetailsRevision { get; private set; }
    public ReservationDispatchState State { get; private set; } = ReservationDispatchState.Pending;
    public long? ResultDetailsRevision { get; private set; }
    public long? ResultReservationVersion { get; private set; }
    public string? ErrorCode { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? SensitiveDataRetainUntilUtc { get; private set; }
    public DateTimeOffset? SensitiveDataRedactedAtUtc { get; private set; }
    public DateTimeOffset? AnonymisedAtUtc { get; private set; }

    public static Result<ReservationDispatch> Create(
        Guid operationId,
        string scopeId,
        Guid sourceLinkId,
        ReservationDispatchTriggerKind triggerKind,
        Guid triggerId,
        Guid receiptId,
        Guid connectionId,
        Guid propertyId,
        Guid? reservationId,
        ReservationDispatchKind kind,
        string? sourceRevision,
        long? sourceSequence,
        string normalizedSnapshot,
        long? expectedDetailsRevision,
        DateTimeOffset nowUtc)
    {
        if (operationId == Guid.Empty || sourceLinkId == Guid.Empty || triggerId == Guid.Empty || receiptId == Guid.Empty ||
            connectionId == Guid.Empty || propertyId == Guid.Empty || string.IsNullOrWhiteSpace(scopeId) ||
            triggerKind is not (ReservationDispatchTriggerKind.Observation or ReservationDispatchTriggerKind.Proposal) ||
            kind is not (ReservationDispatchKind.Create or ReservationDispatchKind.ChangeGuestDetails or ReservationDispatchKind.Cancel or ReservationDispatchKind.Amend) ||
            reservationId == Guid.Empty ||
            (kind == ReservationDispatchKind.Create && (reservationId.HasValue || expectedDetailsRevision.HasValue)) ||
            (kind != ReservationDispatchKind.Create && (!reservationId.HasValue || expectedDetailsRevision <= 0)) ||
            sourceRevision?.Trim().Length > SourceRevisionMaxLength || sourceSequence < 0 ||
            string.IsNullOrWhiteSpace(normalizedSnapshot) || normalizedSnapshot.Length > NormalizedSnapshotMaxLength ||
            nowUtc == default)
        {
            return Result.Failure<ReservationDispatch>(IngestionDomainErrors.ReservationDispatchInvalid);
        }

        return Result.Success(new ReservationDispatch(operationId, scopeId.Trim())
        {
            SourceLinkId = sourceLinkId,
            TriggerKind = triggerKind,
            TriggerId = triggerId,
            ReceiptId = receiptId,
            ConnectionId = connectionId,
            PropertyId = propertyId,
            ReservationId = reservationId,
            Kind = kind,
            SourceRevision = string.IsNullOrWhiteSpace(sourceRevision) ? null : sourceRevision.Trim(),
            SourceSequence = sourceSequence,
            NormalizedSnapshot = normalizedSnapshot,
            ExpectedDetailsRevision = expectedDetailsRevision,
            CreatedAtUtc = nowUtc
        });
    }

    public Result Complete(
        ReservationDispatchState state,
        Guid? reservationId,
        long? detailsRevision,
        long? reservationVersion,
        string? errorCode,
        DateTimeOffset? sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc)
    {
        if (this.State != ReservationDispatchState.Pending ||
            state is not (ReservationDispatchState.Accepted or ReservationDispatchState.Applied or
                ReservationDispatchState.Unchanged or ReservationDispatchState.ProposalRequired or
                ReservationDispatchState.Rejected or ReservationDispatchState.Conflict) ||
            (state == ReservationDispatchState.Accepted && this.Kind != ReservationDispatchKind.Cancel))
        {
            return Result.Failure(IngestionDomainErrors.ReservationDispatchNotPending);
        }

        string? normalizedError = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim();
        bool applied = state is ReservationDispatchState.Accepted or
            ReservationDispatchState.Applied or ReservationDispatchState.Unchanged;
        if (normalizedError?.Length > ErrorCodeMaxLength || reservationId == Guid.Empty ||
            detailsRevision <= 0 || reservationVersion <= 0 || nowUtc < this.CreatedAtUtc ||
            (this.ReservationId.HasValue && reservationId.HasValue &&
                this.ReservationId != reservationId) ||
            (!this.ReservationId.HasValue && reservationId.HasValue && !applied) ||
            (applied && (!reservationId.HasValue || !detailsRevision.HasValue ||
                !reservationVersion.HasValue || normalizedError is not null)))
        {
            return Result.Failure(IngestionDomainErrors.ReservationDispatchInvalid);
        }

        bool remainsActive = state == ReservationDispatchState.Accepted;
        if ((remainsActive && sensitiveDataRetainUntilUtc.HasValue) ||
            (!remainsActive && (!sensitiveDataRetainUntilUtc.HasValue || sensitiveDataRetainUntilUtc <= nowUtc)))
        {
            return Result.Failure(IngestionDomainErrors.SensitiveHistoryRetentionInvalid);
        }

        this.State = state;
        this.ReservationId = reservationId ?? this.ReservationId;
        this.ResultDetailsRevision = detailsRevision;
        this.ResultReservationVersion = reservationVersion;
        this.ErrorCode = normalizedError;
        this.CompletedAtUtc = nowUtc;
        this.SensitiveDataRetainUntilUtc = sensitiveDataRetainUntilUtc;
        this.Version++;
        return Result.Success();
    }

    public Result ConfirmAcceptedCancellation(
        long reservationVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc)
    {
        if (this.Kind != ReservationDispatchKind.Cancel || this.State != ReservationDispatchState.Accepted ||
            reservationVersion <= 0 || !this.CompletedAtUtc.HasValue || nowUtc < this.CompletedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.ReservationDispatchNotPending);
        }

        if (sensitiveDataRetainUntilUtc <= nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.SensitiveHistoryRetentionInvalid);
        }

        this.State = ReservationDispatchState.Applied;
        this.ResultReservationVersion = reservationVersion;
        this.CompletedAtUtc = nowUtc;
        this.SensitiveDataRetainUntilUtc = sensitiveDataRetainUntilUtc;
        this.Version++;
        return Result.Success();
    }

    public Result RedactSensitiveData(DateTimeOffset nowUtc)
    {
        if (this.NormalizedSnapshot is null && this.SensitiveDataRedactedAtUtc.HasValue)
        {
            return Result.Success();
        }

        if (this.State is ReservationDispatchState.Pending or ReservationDispatchState.Accepted ||
            this.NormalizedSnapshot is null || !this.SensitiveDataRetainUntilUtc.HasValue ||
            this.SensitiveDataRetainUntilUtc.Value > nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.SensitiveHistoryNotRedactable);
        }

        this.NormalizedSnapshot = null;
        this.SensitiveDataRedactedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Anonymise(DateTimeOffset nowUtc)
    {
        if (this.State is ReservationDispatchState.Pending or
                ReservationDispatchState.Accepted ||
            this.AnonymisedAtUtc.HasValue ||
            !this.CompletedAtUtc.HasValue || nowUtc < this.CompletedAtUtc)
        {
            return Result.Failure(
                IngestionDomainErrors.AnonymisationRecordNotReducible);
        }

        this.ReservationId = null;
        this.SourceRevision = null;
        this.NormalizedSnapshot = null;
        this.SensitiveDataRedactedAtUtc = nowUtc.ToUniversalTime();
        this.AnonymisedAtUtc = nowUtc.ToUniversalTime();
        this.Version++;
        return Result.Success();
    }
}
