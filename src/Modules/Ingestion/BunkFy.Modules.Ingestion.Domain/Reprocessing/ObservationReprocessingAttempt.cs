namespace BunkFy.Modules.Ingestion.Domain.Reprocessing;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Ingestion.Domain.Errors;

public sealed class ObservationReprocessingAttempt : ScopedAggregateRoot<Guid>
{
    public const int ParserTypeMaxLength = 100;
    public const int RequestedByMaxLength = 200;
    public const int ErrorCodeMaxLength = 200;

    private ObservationReprocessingAttempt() { }

    private ObservationReprocessingAttempt(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid PropertyId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid SourceReceiptId { get; private set; }
    public Guid TaskRunId { get; private set; }
    public string ParserType { get; private set; } = string.Empty;
    public int ParserVersion { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public ObservationReprocessingState State { get; private set; } = ObservationReprocessingState.Queued;
    public int LastTaskAttempt { get; private set; }
    public int ParsedCount { get; private set; }
    public int AcceptedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int RejectedCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset ReservationExpiresAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<ObservationReprocessingAttempt> Create(
        Guid attemptId,
        string scopeId,
        Guid propertyId,
        Guid connectionId,
        Guid sourceReceiptId,
        Guid taskRunId,
        string parserType,
        int parserVersion,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset reservationExpiresAtUtc)
    {
        string normalizedScope = scopeId?.Trim() ?? string.Empty;
        string normalizedParser = parserType?.Trim().ToLowerInvariant() ?? string.Empty;
        string normalizedActor = requestedBy?.Trim() ?? string.Empty;
        if (attemptId == Guid.Empty || propertyId == Guid.Empty || connectionId == Guid.Empty ||
            sourceReceiptId == Guid.Empty || taskRunId == Guid.Empty || attemptId != taskRunId ||
            normalizedScope.Length == 0)
        {
            return Result.Failure<ObservationReprocessingAttempt>(IngestionDomainErrors.ReprocessingIdentityInvalid);
        }

        if (normalizedParser.Length is 0 or > ParserTypeMaxLength || parserVersion <= 0)
        {
            return Result.Failure<ObservationReprocessingAttempt>(IngestionDomainErrors.ReprocessingParserInvalid);
        }

        if (normalizedActor.Length is 0 or > RequestedByMaxLength || requestedAtUtc == default ||
            reservationExpiresAtUtc <= requestedAtUtc)
        {
            return Result.Failure<ObservationReprocessingAttempt>(IngestionDomainErrors.ReprocessingRequestInvalid);
        }

        return Result.Success(new ObservationReprocessingAttempt(attemptId, normalizedScope)
        {
            PropertyId = propertyId,
            ConnectionId = connectionId,
            SourceReceiptId = sourceReceiptId,
            TaskRunId = taskRunId,
            ParserType = normalizedParser,
            ParserVersion = parserVersion,
            RequestedBy = normalizedActor,
            RequestedAtUtc = requestedAtUtc,
            ReservationExpiresAtUtc = reservationExpiresAtUtc
        });
    }

    public Result Start(Guid taskRunId, int taskAttempt, DateTimeOffset nowUtc, DateTimeOffset reservationExpiresAtUtc)
    {
        if (taskRunId != this.TaskRunId || taskAttempt <= 0 ||
            nowUtc < (this.StartedAtUtc ?? this.RequestedAtUtc) || reservationExpiresAtUtc <= nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingTaskInvalid);
        }

        if (this.State is not (ObservationReprocessingState.Queued or ObservationReprocessingState.Running))
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingNotActive);
        }

        if (taskAttempt < this.LastTaskAttempt)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingTaskInvalid);
        }

        this.State = ObservationReprocessingState.Running;
        this.LastTaskAttempt = taskAttempt;
        this.StartedAtUtc ??= nowUtc;
        this.ReservationExpiresAtUtc = reservationExpiresAtUtc;
        this.LastErrorCode = null;
        this.Version++;
        return Result.Success();
    }

    public Result ScheduleRetry(
        int taskAttempt,
        string errorCode,
        DateTimeOffset nowUtc,
        DateTimeOffset reservationExpiresAtUtc)
    {
        if (this.State != ObservationReprocessingState.Running || taskAttempt != this.LastTaskAttempt ||
            !this.StartedAtUtc.HasValue || nowUtc < this.StartedAtUtc || reservationExpiresAtUtc <= nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingTaskInvalid);
        }

        string normalizedError = NormalizeError(errorCode);
        if (normalizedError.Length == 0)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingErrorInvalid);
        }

        this.State = ObservationReprocessingState.Queued;
        this.LastErrorCode = normalizedError;
        this.ReservationExpiresAtUtc = reservationExpiresAtUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Complete(
        int parsedCount,
        int acceptedCount,
        int duplicateCount,
        int rejectedCount,
        bool noMatch,
        string? reasonCode,
        DateTimeOffset nowUtc)
    {
        if (this.State != ObservationReprocessingState.Running || parsedCount < 0 || acceptedCount < 0 ||
            duplicateCount < 0 || rejectedCount < 0 ||
            (long)acceptedCount + duplicateCount + rejectedCount != parsedCount ||
            (noMatch && parsedCount != 0) || !this.StartedAtUtc.HasValue || nowUtc < this.StartedAtUtc)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingOutcomeInvalid);
        }

        string? normalizedReason = string.IsNullOrWhiteSpace(reasonCode) ? null : NormalizeError(reasonCode);
        bool requiresReason = noMatch || rejectedCount > 0;
        if (requiresReason != (normalizedReason is not null))
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingErrorInvalid);
        }

        this.State = noMatch
            ? ObservationReprocessingState.NoMatch
            : rejectedCount > 0
                ? ObservationReprocessingState.Failed
                : ObservationReprocessingState.Succeeded;
        this.ParsedCount = parsedCount;
        this.AcceptedCount = acceptedCount;
        this.DuplicateCount = duplicateCount;
        this.RejectedCount = rejectedCount;
        this.LastErrorCode = normalizedReason;
        this.CompletedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Fail(string errorCode, DateTimeOffset nowUtc)
    {
        if (this.State is not (ObservationReprocessingState.Queued or ObservationReprocessingState.Running) ||
            nowUtc < (this.StartedAtUtc ?? this.RequestedAtUtc))
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingNotActive);
        }

        string normalizedError = NormalizeError(errorCode);
        if (normalizedError.Length == 0)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingErrorInvalid);
        }

        this.State = ObservationReprocessingState.Failed;
        this.LastErrorCode = normalizedError;
        this.CompletedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset nowUtc) => this.Terminate(
        ObservationReprocessingState.Canceled,
        "ingestion.reprocessing-canceled",
        nowUtc);

    public Result Expire(DateTimeOffset nowUtc)
    {
        if (this.ReservationExpiresAtUtc > nowUtc)
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingReservationActive);
        }

        return this.Terminate(
            ObservationReprocessingState.Expired,
            "ingestion.reprocessing-reservation-expired",
            nowUtc);
    }

    private Result Terminate(ObservationReprocessingState state, string errorCode, DateTimeOffset nowUtc)
    {
        if (this.State is not (ObservationReprocessingState.Queued or ObservationReprocessingState.Running) ||
            nowUtc < (this.StartedAtUtc ?? this.RequestedAtUtc))
        {
            return Result.Failure(IngestionDomainErrors.ReprocessingNotActive);
        }

        this.State = state;
        this.LastErrorCode = errorCode;
        this.CompletedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    private static string NormalizeError(string? errorCode)
    {
        string normalized = errorCode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length <= ErrorCodeMaxLength ? normalized : normalized[..ErrorCodeMaxLength];
    }
}
