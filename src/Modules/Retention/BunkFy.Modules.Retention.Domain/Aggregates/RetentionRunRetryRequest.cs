namespace BunkFy.Modules.Retention.Domain.Aggregates;

using BunkFy.Modules.Retention.Domain.Errors;
using BunkFy.Modules.Retention.Domain.Events;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class RetentionRunRetryRequest : ScopedAggregateRoot<Guid>
{
    public const int FailureCodeMaxLength = 100;

    private RetentionRunRetryRequest() { }

    private RetentionRunRetryRequest(Guid id, string scopeId)
        : base(id, scopeId) { }

    public Guid RunId { get; private set; }
    public string OwnerKey { get; private set; } = string.Empty;
    public string DataClassKey { get; private set; } = string.Empty;
    public RetentionExecutionTargetKind TargetKind { get; private set; }
    public Guid? PropertyId { get; private set; }
    public int ExecutionPolicyVersion { get; private set; }
    public long EvidenceVersion { get; private set; }
    public int Attempt { get; private set; }
    public RetentionRunRetryRequestState State { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? ScheduledAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? FailureCode { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<RetentionRunRetryRequest> Create(
        Guid id,
        Guid eventId,
        string tenantId,
        Guid runId,
        string ownerKey,
        string dataClassKey,
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId,
        int executionPolicyVersion,
        long evidenceVersion,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset? scheduledAtUtc)
    {
        if (id == Guid.Empty ||
            eventId == Guid.Empty ||
            runId == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            !TryNormalizeKey(ownerKey, out string? normalizedOwner) ||
            !TryNormalizeKey(dataClassKey, out string? normalizedDataClass) ||
            !IsValidTarget(targetKind, propertyId) ||
            executionPolicyVersion <= 0 ||
            evidenceVersion <= 0 ||
            requestedAtUtc == default ||
            scheduledAtUtc < requestedAtUtc)
        {
            return Result.Failure<RetentionRunRetryRequest>(
                RetentionDomainErrors.RecoveryRequestInvalid);
        }

        RetentionRunRetryRequest request = new(id, scopeId!)
        {
            RunId = runId,
            OwnerKey = normalizedOwner!,
            DataClassKey = normalizedDataClass!,
            TargetKind = targetKind,
            PropertyId = propertyId,
            ExecutionPolicyVersion = executionPolicyVersion,
            EvidenceVersion = evidenceVersion,
            Attempt = 1,
            State = RetentionRunRetryRequestState.Pending,
            RequestedAtUtc = requestedAtUtc,
            ScheduledAtUtc = scheduledAtUtc
        };
        request.RaiseRequested(eventId, requestedAtUtc);
        return Result.Success(request);
    }

    public Result RequestAgain(
        Guid eventId,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset? scheduledAtUtc)
    {
        if (this.State != RetentionRunRetryRequestState.Failed ||
            eventId == Guid.Empty ||
            requestedAtUtc < this.RequestedAtUtc ||
            scheduledAtUtc < requestedAtUtc ||
            this.Attempt == int.MaxValue ||
            this.Version == long.MaxValue)
        {
            return Result.Failure(
                RetentionDomainErrors.RecoveryTransitionInvalid);
        }

        this.Attempt++;
        this.State = RetentionRunRetryRequestState.Pending;
        this.RequestedAtUtc = requestedAtUtc;
        this.ScheduledAtUtc = scheduledAtUtc;
        this.CompletedAtUtc = null;
        this.FailureCode = null;
        this.Version++;
        this.RaiseRequested(eventId, requestedAtUtc);
        return Result.Success();
    }

    public Result MarkApplied(DateTimeOffset completedAtUtc)
    {
        if (this.State == RetentionRunRetryRequestState.Applied)
        {
            return Result.Success();
        }

        if (this.State != RetentionRunRetryRequestState.Pending ||
            completedAtUtc < this.RequestedAtUtc ||
            this.Version == long.MaxValue)
        {
            return Result.Failure(
                RetentionDomainErrors.RecoveryTransitionInvalid);
        }

        this.State = RetentionRunRetryRequestState.Applied;
        this.CompletedAtUtc = completedAtUtc;
        this.FailureCode = null;
        this.Version++;
        return Result.Success();
    }

    public Result MarkFailed(string failureCode, DateTimeOffset completedAtUtc)
    {
        string normalized = NormalizeFailureCode(failureCode);
        if (this.State == RetentionRunRetryRequestState.Failed &&
            string.Equals(this.FailureCode, normalized, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        if (this.State != RetentionRunRetryRequestState.Pending ||
            normalized.Length == 0 ||
            completedAtUtc < this.RequestedAtUtc ||
            this.Version == long.MaxValue)
        {
            return Result.Failure(
                RetentionDomainErrors.RecoveryTransitionInvalid);
        }

        this.State = RetentionRunRetryRequestState.Failed;
        this.CompletedAtUtc = completedAtUtc;
        this.FailureCode = normalized;
        this.Version++;
        return Result.Success();
    }

    public bool MatchesEvidence(
        Guid runId,
        string ownerKey,
        string dataClassKey,
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId,
        int executionPolicyVersion,
        long evidenceVersion) =>
        runId == this.RunId &&
        TryNormalizeKey(ownerKey, out string? normalizedOwner) &&
        TryNormalizeKey(dataClassKey, out string? normalizedDataClass) &&
        string.Equals(this.OwnerKey, normalizedOwner, StringComparison.Ordinal) &&
        string.Equals(
            this.DataClassKey,
            normalizedDataClass,
            StringComparison.Ordinal) &&
        this.TargetKind == targetKind &&
        this.PropertyId == propertyId &&
        this.ExecutionPolicyVersion == executionPolicyVersion &&
        this.EvidenceVersion == evidenceVersion;

    private void RaiseRequested(Guid eventId, DateTimeOffset occurredAtUtc) =>
        this.RaiseDomainEvent(new RetentionRunRetryRequestedDomainEvent(
            eventId,
            occurredAtUtc,
            this.ScopeId,
            this.Id,
            this.RunId,
            this.Attempt));

    private static bool IsValidTarget(
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId) => targetKind switch
        {
            RetentionExecutionTargetKind.Tenant => propertyId is null,
            RetentionExecutionTargetKind.Property =>
                propertyId is not null && propertyId != Guid.Empty,
            _ => false
        };

    private static bool TryNormalizeKey(string value, out string? normalized)
    {
        normalized = value?.Trim().ToLowerInvariant();
        return normalized is
        { Length: > 0 and <= RetentionExecution.KeyMaxLength } &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '.');
    }

    private static string NormalizeFailureCode(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= FailureCodeMaxLength &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '.')
            ? normalized
            : string.Empty;
    }
}
