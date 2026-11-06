namespace BunkFy.Modules.Ingestion.Domain.Reservations;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Ingestion.Domain.Errors;

public sealed class ReservationSourceLink : ScopedAggregateRoot<Guid>
{
    public const int SourceSystemMaxLength = 100;
    public const int SourceReferenceMaxLength = 512;
    public const int SourceRevisionMaxLength = 256;
    public const int ContentHashLength = 64;
    public const int OperationalBaselineMaxLength = 16_384;

    private ReservationSourceLink() { }

    private ReservationSourceLink(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid PropertyId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string SourceSystem { get; private set; } = string.Empty;
    public string SourceReference { get; private set; } = string.Empty;
    public Guid? ReservationId { get; private set; }
    public ReservationSourceLinkState State { get; private set; } = ReservationSourceLinkState.AwaitingCreate;
    public Guid LastObservedReceiptId { get; private set; }
    public string? LastObservedSourceRevision { get; private set; }
    public long? LastObservedSourceSequence { get; private set; }
    public DateTimeOffset? LastObservedSourceUpdatedAtUtc { get; private set; }
    public string LastObservedContentHash { get; private set; } = string.Empty;
    public Guid? LastAppliedReceiptId { get; private set; }
    public string? LastAppliedSourceRevision { get; private set; }
    public long? LastAppliedSourceSequence { get; private set; }
    public long? LastAppliedReservationDetailsRevision { get; private set; }
    public string? LastAppliedOperationalBaseline { get; private set; }
    public Guid? LastProductOperationId { get; private set; }
    public Guid? ActiveProductOperationId { get; private set; }
    public Guid? DeferredReceiptId { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public DateTimeOffset? AnonymisedAtUtc { get; private set; }

    public static Result<ReservationSourceLink> Create(
        Guid linkId,
        string scopeId,
        Guid propertyId,
        Guid connectionId,
        string sourceSystem,
        string sourceReference,
        DateTimeOffset nowUtc)
    {
        string normalizedScope = scopeId?.Trim() ?? string.Empty;
        string normalizedSystem = sourceSystem?.Trim().ToLowerInvariant() ?? string.Empty;
        string normalizedReference = sourceReference?.Trim() ?? string.Empty;
        if (linkId == Guid.Empty || propertyId == Guid.Empty || connectionId == Guid.Empty ||
            normalizedScope.Length == 0 || normalizedSystem.Length is 0 or > SourceSystemMaxLength ||
            normalizedReference.Length is 0 or > SourceReferenceMaxLength || nowUtc == default)
        {
            return Result.Failure<ReservationSourceLink>(IngestionDomainErrors.ReservationSourceLinkInvalid);
        }

        return Result.Success(new ReservationSourceLink(linkId, normalizedScope)
        {
            PropertyId = propertyId,
            ConnectionId = connectionId,
            SourceSystem = normalizedSystem,
            SourceReference = normalizedReference,
            CreatedAtUtc = nowUtc
        });
    }

    public Result<ReservationObservationResult> Observe(
        Guid receiptId,
        string? sourceRevision,
        long? sourceSequence,
        DateTimeOffset? sourceUpdatedAtUtc,
        string contentHash,
        DateTimeOffset nowUtc)
    {
        string? normalizedRevision = string.IsNullOrWhiteSpace(sourceRevision) ? null : sourceRevision.Trim();
        string normalizedHash = contentHash?.Trim().ToLowerInvariant() ?? string.Empty;
        if (receiptId == Guid.Empty || normalizedRevision?.Length > SourceRevisionMaxLength || sourceSequence < 0 ||
            normalizedHash.Length != ContentHashLength || !normalizedHash.All(Uri.IsHexDigit) ||
            nowUtc < (this.UpdatedAtUtc ?? this.CreatedAtUtc))
        {
            return Result.Failure<ReservationObservationResult>(IngestionDomainErrors.ReservationObservationInvalid);
        }

        if (receiptId == this.LastObservedReceiptId ||
            (normalizedRevision is not null && normalizedRevision == this.LastObservedSourceRevision &&
             normalizedHash == this.LastObservedContentHash))
        {
            return Result.Success(new ReservationObservationResult(ReservationObservationDisposition.Replay, null));
        }

        if ((sourceSequence.HasValue && this.LastObservedSourceSequence.HasValue &&
             sourceSequence.Value <= this.LastObservedSourceSequence.Value) ||
            (!sourceSequence.HasValue && !this.LastObservedSourceSequence.HasValue && sourceUpdatedAtUtc.HasValue &&
             this.LastObservedSourceUpdatedAtUtc.HasValue && sourceUpdatedAtUtc.Value <= this.LastObservedSourceUpdatedAtUtc.Value))
        {
            return Result.Success(new ReservationObservationResult(ReservationObservationDisposition.Stale, null));
        }

        bool requiresReview = this.LastObservedReceiptId != Guid.Empty &&
            ((sourceSequence.HasValue != this.LastObservedSourceSequence.HasValue) ||
             (!sourceSequence.HasValue && (!sourceUpdatedAtUtc.HasValue || !this.LastObservedSourceUpdatedAtUtc.HasValue)));

        Guid? superseded = this.DeferredReceiptId;
        this.LastObservedReceiptId = receiptId;
        this.LastObservedSourceRevision = normalizedRevision;
        this.LastObservedSourceSequence = sourceSequence;
        this.LastObservedSourceUpdatedAtUtc = sourceUpdatedAtUtc;
        this.LastObservedContentHash = normalizedHash;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        if (this.ActiveProductOperationId.HasValue)
        {
            this.DeferredReceiptId = receiptId;
            return Result.Success(new ReservationObservationResult(ReservationObservationDisposition.Deferred, superseded));
        }

        return Result.Success(new ReservationObservationResult(
            requiresReview ? ReservationObservationDisposition.RequiresReview : ReservationObservationDisposition.Ready,
            superseded));
    }

    public Result BeginDispatch(Guid operationId, DateTimeOffset nowUtc)
    {
        if (operationId == Guid.Empty || nowUtc < (this.UpdatedAtUtc ?? this.CreatedAtUtc))
        {
            return Result.Failure(IngestionDomainErrors.IdRequired);
        }

        if (this.ActiveProductOperationId.HasValue)
        {
            return Result.Failure(IngestionDomainErrors.ReservationOperationActive);
        }

        this.ActiveProductOperationId = operationId;
        if (this.DeferredReceiptId == this.LastObservedReceiptId)
        {
            this.DeferredReceiptId = null;
        }

        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result CompleteDispatch(
        Guid operationId,
        Guid receiptId,
        string? sourceRevision,
        long? sourceSequence,
        string? operationalBaseline,
        Guid? reservationId,
        long? detailsRevision,
        bool keepActive,
        bool applied,
        bool cancellationPending,
        bool cancelled,
        DateTimeOffset nowUtc)
    {
        if (this.ActiveProductOperationId != operationId)
        {
            return Result.Failure(IngestionDomainErrors.ReservationOperationMismatch);
        }

        string? normalizedRevision = string.IsNullOrWhiteSpace(sourceRevision)
            ? null
            : sourceRevision.Trim();
        bool cancellation = cancellationPending || cancelled;
        if (receiptId == Guid.Empty || normalizedRevision?.Length > SourceRevisionMaxLength ||
            sourceSequence < 0 || reservationId == Guid.Empty || detailsRevision <= 0 ||
            (cancellationPending && cancelled) || keepActive != cancellationPending ||
            (cancellation && !applied) || nowUtc < (this.UpdatedAtUtc ?? this.CreatedAtUtc) ||
            (this.ReservationId.HasValue && reservationId.HasValue &&
                this.ReservationId != reservationId) ||
            (!applied && !this.ReservationId.HasValue && reservationId.HasValue) ||
            operationalBaseline?.Length > OperationalBaselineMaxLength ||
            (applied && !cancellation && string.IsNullOrWhiteSpace(operationalBaseline)) ||
            (cancellation && operationalBaseline is not null) ||
            (applied && (!reservationId.HasValue || !detailsRevision.HasValue)))
        {
            return Result.Failure(IngestionDomainErrors.ReservationObservationInvalid);
        }

        if (applied && reservationId.HasValue)
        {
            this.ReservationId = reservationId;
        }

        if (applied)
        {
            this.LastAppliedReceiptId = receiptId;
            this.LastAppliedSourceRevision = normalizedRevision;
            this.LastAppliedSourceSequence = sourceSequence;
            this.LastAppliedReservationDetailsRevision = detailsRevision;
            this.LastAppliedOperationalBaseline = operationalBaseline;
            this.LastProductOperationId = operationId;
        }

        this.State = cancelled
            ? ReservationSourceLinkState.Cancelled
            : cancellationPending
                ? ReservationSourceLinkState.CancellationPending
                : this.ReservationId.HasValue
                    ? ReservationSourceLinkState.Linked
                    : ReservationSourceLinkState.AwaitingCreate;
        if (!keepActive)
        {
            this.ActiveProductOperationId = null;
        }

        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result CompleteAcceptedCancellation(Guid reservationId, DateTimeOffset nowUtc)
    {
        if (this.ReservationId != reservationId || this.State != ReservationSourceLinkState.CancellationPending ||
            !this.ActiveProductOperationId.HasValue || reservationId == Guid.Empty ||
            nowUtc < (this.UpdatedAtUtc ?? this.CreatedAtUtc))
        {
            return Result.Failure(IngestionDomainErrors.ReservationOperationMismatch);
        }

        this.State = ReservationSourceLinkState.Cancelled;
        this.ActiveProductOperationId = null;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Anonymise(long expectedVersion, DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version ||
            this.State != ReservationSourceLinkState.Cancelled ||
            this.ActiveProductOperationId.HasValue ||
            this.DeferredReceiptId.HasValue ||
            this.AnonymisedAtUtc.HasValue ||
            nowUtc < (this.UpdatedAtUtc ?? this.CreatedAtUtc))
        {
            return Result.Failure(
                IngestionDomainErrors.AnonymisationRecordNotReducible);
        }

        this.SourceReference = $"anonymised:{this.Id:N}";
        this.ReservationId = null;
        this.LastObservedSourceRevision = null;
        this.LastObservedContentHash =
            new string('0', ContentHashLength);
        this.LastAppliedSourceRevision = null;
        this.LastAppliedOperationalBaseline = null;
        this.State = ReservationSourceLinkState.Anonymised;
        this.AnonymisedAtUtc = nowUtc.ToUniversalTime();
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }
}

public sealed record ReservationObservationResult(
    ReservationObservationDisposition Disposition,
    Guid? SupersededReceiptId);
