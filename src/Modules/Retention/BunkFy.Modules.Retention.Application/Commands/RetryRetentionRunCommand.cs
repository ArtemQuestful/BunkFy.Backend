namespace BunkFy.Modules.Retention.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;

public sealed record RetentionRunRetryExpectation(
    string OwnerKey,
    string DataClassKey,
    RetentionTargetScopeKind TargetScopeKind,
    Guid? PropertyId,
    int ExecutionPolicyVersion,
    long EvidenceVersion);

public sealed record RequestRetentionRunRetryCommand(
    Guid RunId,
    string TenantId,
    DateTimeOffset? ScheduledAtUtc,
    RetentionRunRetryExpectation? ExpectedCurrentSchedule = null)
    : ITransactionalCommand<RetentionRunRetryReceiptDto>;
