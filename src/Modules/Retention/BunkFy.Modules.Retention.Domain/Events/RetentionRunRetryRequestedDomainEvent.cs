namespace BunkFy.Modules.Retention.Domain.Events;

using Gma.Framework.Domain;

public sealed record RetentionRunRetryRequestedDomainEvent : ScopedDomainEvent
{
    public RetentionRunRetryRequestedDomainEvent(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        string tenantId,
        Guid requestId,
        Guid runId,
        int attempt)
        : base(eventId, occurredAtUtc, tenantId)
    {
        this.RequestId = requestId != Guid.Empty
            ? requestId
            : throw new ArgumentException(
                "A retry request id is required.",
                nameof(requestId));
        this.RunId = runId != Guid.Empty
            ? runId
            : throw new ArgumentException(
                "A retry run id is required.",
                nameof(runId));
        this.Attempt = attempt > 0
            ? attempt
            : throw new ArgumentOutOfRangeException(nameof(attempt));
    }

    public Guid RequestId { get; }
    public Guid RunId { get; }
    public int Attempt { get; }
}
