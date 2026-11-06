namespace BunkFy.Modules.Retention.Contracts;

using Gma.Framework.Messaging;
using Gma.Framework.Tenancy;
using Gma.Framework.Tenancy.Messaging;

[IntegrationEventName(EventType)]
[IntegrationEventVersion(EventVersion)]
[TenantScoped]
public sealed record RetentionRunRetryRequestedIntegrationEvent
    : TenantIntegrationEvent
{
    public const string EventType = "retention-run-retry-requested";
    public const int EventVersion = 1;

    public RetentionRunRetryRequestedIntegrationEvent(
        Guid eventId,
        string tenantId,
        DateTimeOffset occurredAtUtc,
        Guid requestId,
        Guid runId,
        int attempt)
        : base(eventId, tenantId, occurredAtUtc, EventType, EventVersion)
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
