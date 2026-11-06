namespace BunkFy.Modules.Ingestion.Domain.Proposals;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Ingestion.Domain.Errors;

public sealed class ChangeProposal : ScopedAggregateRoot<Guid>
{
    public const int DiffMaxLength = 32_768;
    public const int ReasonCodeMaxLength = 100;
    public const int ActorMaxLength = 200;
    public const int ReasonMaxLength = 2000;

    private ChangeProposal() { }

    private ChangeProposal(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid PropertyId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid SourcePayloadFileId { get; private set; }
    public long BaseReservationDetailsRevision { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public string? Diff { get; private set; }
    public ChangeProposalState State { get; private set; } = ChangeProposalState.Pending;
    public string? DecisionActor { get; private set; }
    public string? DecisionReason { get; private set; }
    public Guid? ProductOperationId { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? SensitiveDataRetainUntilUtc { get; private set; }
    public DateTimeOffset? SensitiveDataRedactedAtUtc { get; private set; }
    public DateTimeOffset? AnonymisedAtUtc { get; private set; }

    public static Result<ChangeProposal> Create(
        Guid proposalId,
        string scopeId,
        Guid propertyId,
        Guid connectionId,
        Guid receiptId,
        Guid reservationId,
        Guid sourcePayloadFileId,
        long baseReservationDetailsRevision,
        string reasonCode,
        string diff,
        DateTimeOffset nowUtc)
    {
        if (proposalId == Guid.Empty || receiptId == Guid.Empty || reservationId == Guid.Empty || sourcePayloadFileId == Guid.Empty)
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.ProposalIdentityInvalid);
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.ScopeRequired);
        }

        if (propertyId == Guid.Empty)
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.PropertyIdRequired);
        }

        if (connectionId == Guid.Empty)
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.ConnectionIdRequired);
        }

        string normalizedReasonCode = reasonCode?.Trim().ToLowerInvariant() ?? string.Empty;
        string normalizedDiff = diff?.Trim() ?? string.Empty;
        if (normalizedReasonCode.Length is 0 or > ReasonCodeMaxLength)
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.ProposalReasonCodeInvalid);
        }

        if (baseReservationDetailsRevision <= 0 || normalizedDiff.Length is 0 or > DiffMaxLength ||
            nowUtc == default)
        {
            return Result.Failure<ChangeProposal>(IngestionDomainErrors.ProposalDiffInvalid);
        }

        return Result.Success(new ChangeProposal(proposalId, scopeId.Trim())
        {
            PropertyId = propertyId,
            ConnectionId = connectionId,
            ReceiptId = receiptId,
            ReservationId = reservationId,
            SourcePayloadFileId = sourcePayloadFileId,
            BaseReservationDetailsRevision = baseReservationDetailsRevision,
            ReasonCode = normalizedReasonCode,
            Diff = normalizedDiff,
            CreatedAtUtc = nowUtc
        });
    }

    public Result BeginApply(string actor, Guid productOperationId, long expectedVersion, DateTimeOffset nowUtc)
    {
        Result pending = this.EnsurePending(expectedVersion);
        if (pending.IsFailure)
        {
            return pending;
        }

        string normalizedActor = actor?.Trim() ?? string.Empty;
        if (normalizedActor.Length is 0 or > ActorMaxLength || productOperationId == Guid.Empty ||
            nowUtc < this.CreatedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.DecisionReasonInvalid);
        }

        this.State = ChangeProposalState.Applying;
        this.DecisionActor = normalizedActor;
        this.ProductOperationId = productOperationId;
        this.DecidedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result MarkApplied(
        Guid productOperationId,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc)
    {
        Result applying = this.EnsureApplying(expectedVersion);
        if (applying.IsFailure)
        {
            return applying;
        }

        if (productOperationId == Guid.Empty || this.ProductOperationId != productOperationId)
        {
            return Result.Failure(IngestionDomainErrors.IdRequired);
        }

        if (!this.DecidedAtUtc.HasValue || nowUtc < this.DecidedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.ProposalNotApplying);
        }

        Result retention = ValidateRetentionDeadline(sensitiveDataRetainUntilUtc, nowUtc);
        if (retention.IsFailure)
        {
            return retention;
        }

        this.State = ChangeProposalState.Applied;
        this.CompletedAtUtc = nowUtc;
        this.SensitiveDataRetainUntilUtc = sensitiveDataRetainUntilUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Reject(
        string actor,
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc) =>
        this.CompletePending(
            ChangeProposalState.Rejected,
            actor,
            reason,
            expectedVersion,
            sensitiveDataRetainUntilUtc,
            nowUtc);

    public Result Supersede(
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc) =>
        this.CompletePending(
            ChangeProposalState.Superseded,
            "system",
            reason,
            expectedVersion,
            sensitiveDataRetainUntilUtc,
            nowUtc);

    public Result MarkStale(
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc) =>
        this.CompleteApplying(
            ChangeProposalState.Stale,
            reason,
            expectedVersion,
            sensitiveDataRetainUntilUtc,
            nowUtc);

    public Result MarkFailed(
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc) =>
        this.CompleteApplying(
            ChangeProposalState.Failed,
            reason,
            expectedVersion,
            sensitiveDataRetainUntilUtc,
            nowUtc);

    public Result RedactSensitiveData(DateTimeOffset nowUtc)
    {
        if (this.Diff is null && this.SensitiveDataRedactedAtUtc.HasValue)
        {
            return Result.Success();
        }

        if (this.State is ChangeProposalState.Pending or ChangeProposalState.Applying ||
            this.Diff is null || !this.SensitiveDataRetainUntilUtc.HasValue ||
            this.SensitiveDataRetainUntilUtc.Value > nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.SensitiveHistoryNotRedactable);
        }

        this.Diff = null;
        this.SensitiveDataRedactedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Anonymise(DateTimeOffset nowUtc)
    {
        if (this.State is ChangeProposalState.Pending or
                ChangeProposalState.Applying ||
            this.AnonymisedAtUtc.HasValue ||
            !this.CompletedAtUtc.HasValue || nowUtc < this.CompletedAtUtc)
        {
            return Result.Failure(
                IngestionDomainErrors.AnonymisationRecordNotReducible);
        }

        this.ReservationId = Guid.Empty;
        this.Diff = null;
        this.DecisionReason = null;
        this.SensitiveDataRedactedAtUtc = nowUtc.ToUniversalTime();
        this.AnonymisedAtUtc = nowUtc.ToUniversalTime();
        this.Version++;
        return Result.Success();
    }

    private Result CompletePending(
        ChangeProposalState finalState,
        string actor,
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc)
    {
        Result pending = this.EnsurePending(expectedVersion);
        if (pending.IsFailure)
        {
            return pending;
        }

        Result decision = ValidateDecision(actor, reason);
        if (decision.IsFailure)
        {
            return decision;
        }

        if (nowUtc < this.CreatedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.DecisionReasonInvalid);
        }

        Result retention = ValidateRetentionDeadline(sensitiveDataRetainUntilUtc, nowUtc);
        if (retention.IsFailure)
        {
            return retention;
        }

        this.State = finalState;
        this.DecisionActor = actor.Trim();
        this.DecisionReason = reason.Trim();
        this.DecidedAtUtc = nowUtc;
        this.CompletedAtUtc = nowUtc;
        this.SensitiveDataRetainUntilUtc = sensitiveDataRetainUntilUtc;
        this.Version++;
        return Result.Success();
    }

    private Result CompleteApplying(
        ChangeProposalState finalState,
        string reason,
        long expectedVersion,
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc)
    {
        Result applying = this.EnsureApplying(expectedVersion);
        if (applying.IsFailure)
        {
            return applying;
        }

        string normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length is 0 or > ReasonMaxLength)
        {
            return Result.Failure(IngestionDomainErrors.DecisionReasonInvalid);
        }

        if (!this.DecidedAtUtc.HasValue || nowUtc < this.DecidedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.ProposalNotApplying);
        }

        Result retention = ValidateRetentionDeadline(sensitiveDataRetainUntilUtc, nowUtc);
        if (retention.IsFailure)
        {
            return retention;
        }

        this.State = finalState;
        this.DecisionReason = normalizedReason;
        this.CompletedAtUtc = nowUtc;
        this.SensitiveDataRetainUntilUtc = sensitiveDataRetainUntilUtc;
        this.Version++;
        return Result.Success();
    }

    private Result EnsurePending(long expectedVersion)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(IngestionDomainErrors.VersionConflict);
        }

        return this.State == ChangeProposalState.Pending
            ? Result.Success()
            : Result.Failure(IngestionDomainErrors.ProposalNotPending);
    }

    private Result EnsureApplying(long expectedVersion)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(IngestionDomainErrors.VersionConflict);
        }

        return this.State == ChangeProposalState.Applying
            ? Result.Success()
            : Result.Failure(IngestionDomainErrors.ProposalNotApplying);
    }

    private static Result ValidateDecision(string actor, string reason)
    {
        string normalizedActor = actor?.Trim() ?? string.Empty;
        string normalizedReason = reason?.Trim() ?? string.Empty;
        return normalizedActor.Length is 0 or > ActorMaxLength || normalizedReason.Length is 0 or > ReasonMaxLength
            ? Result.Failure(IngestionDomainErrors.DecisionReasonInvalid)
            : Result.Success();
    }

    private static Result ValidateRetentionDeadline(
        DateTimeOffset sensitiveDataRetainUntilUtc,
        DateTimeOffset nowUtc) => sensitiveDataRetainUntilUtc > nowUtc
        ? Result.Success()
        : Result.Failure(IngestionDomainErrors.SensitiveHistoryRetentionInvalid);
}
