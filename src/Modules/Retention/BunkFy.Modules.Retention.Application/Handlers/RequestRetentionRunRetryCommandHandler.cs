namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class RequestRetentionRunRetryCommandHandler(
    IRetentionMutationLock mutationLock,
    IRetentionScheduleHealthReader scheduleHealth,
    IRetentionRunRetryRequestRepository requests,
    IIdGenerator ids,
    ISystemClock clock)
    : ICommandHandler<
        RequestRetentionRunRetryCommand,
        RetentionRunRetryReceiptDto>
{
    public async Task<Result<RetentionRunRetryReceiptDto>> HandleAsync(
        RequestRetentionRunRetryCommand command,
        CancellationToken cancellationToken)
    {
        RetentionScheduleStateSnapshot? candidate;
        if (command.ExpectedCurrentSchedule is { } expected)
        {
            await mutationLock.AcquireScheduleAsync(
                    command.TenantId,
                    expected.OwnerKey,
                    expected.DataClassKey,
                    expected.PropertyId,
                    expected.ExecutionPolicyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            candidate = await scheduleHealth.GetAsync(
                    command.TenantId,
                    expected.OwnerKey,
                    expected.DataClassKey,
                    expected.PropertyId,
                    expected.ExecutionPolicyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            candidate = await scheduleHealth.GetByLastExecutionIdAsync(
                    command.TenantId,
                    command.RunId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                return Unavailable();
            }

            await mutationLock.AcquireScheduleAsync(
                    command.TenantId,
                    candidate.OwnerKey,
                    candidate.DataClassKey,
                    candidate.PropertyId,
                    candidate.ExecutionPolicyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            candidate = await scheduleHealth.GetAsync(
                    command.TenantId,
                    candidate.OwnerKey,
                    candidate.DataClassKey,
                    candidate.PropertyId,
                    candidate.ExecutionPolicyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidate is null)
        {
            return Unavailable();
        }

        if (!IsCurrentFailed(command, candidate))
        {
            return EvidenceChanged();
        }

        RetentionRunRetryRequest? existing =
            await requests.GetForEvidenceAsync(
                    command.RunId,
                    candidate.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        DateTimeOffset nowUtc = clock.UtcNow;
        if (existing is not null)
        {
            if (!existing.MatchesEvidence(
                    command.RunId,
                    candidate.OwnerKey,
                    candidate.DataClassKey,
                    MapTarget(candidate.PropertyId),
                    candidate.PropertyId,
                    candidate.ExecutionPolicyVersion,
                    candidate.Version))
            {
                return EvidenceChanged();
            }

            if (existing.State == RetentionRunRetryRequestState.Failed)
            {
                Result requestedAgain = existing.RequestAgain(
                    ids.NewId(),
                    nowUtc,
                    command.ScheduledAtUtc);
                if (requestedAgain.IsFailure)
                {
                    return Result.Failure<RetentionRunRetryReceiptDto>(
                        requestedAgain.Error);
                }
            }

            return Result.Success(ToReceipt(existing));
        }

        Result<RetentionRunRetryRequest> created =
            RetentionRunRetryRequest.Create(
                ids.NewId(),
                ids.NewId(),
                command.TenantId,
                command.RunId,
                candidate.OwnerKey,
                candidate.DataClassKey,
                MapTarget(candidate.PropertyId),
                candidate.PropertyId,
                candidate.ExecutionPolicyVersion,
                candidate.Version,
                nowUtc,
                command.ScheduledAtUtc);
        if (created.IsFailure)
        {
            return Result.Failure<RetentionRunRetryReceiptDto>(created.Error);
        }

        await requests.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(ToReceipt(created.Value));
    }

    private static bool IsCurrentFailed(
        RequestRetentionRunRetryCommand command,
        RetentionScheduleStateSnapshot? current)
    {
        if (current is null ||
            current.LastExecutionId != command.RunId ||
            current.State != (int)RetentionExecutionState.Failed)
        {
            return false;
        }

        RetentionRunRetryExpectation? expected =
            command.ExpectedCurrentSchedule;
        return expected is null ||
            (string.Equals(
                current.OwnerKey,
                expected.OwnerKey,
                StringComparison.Ordinal) &&
            string.Equals(
                current.DataClassKey,
                expected.DataClassKey,
                StringComparison.Ordinal) &&
            current.PropertyId == expected.PropertyId &&
            current.ExecutionPolicyVersion ==
                expected.ExecutionPolicyVersion &&
            current.Version == expected.EvidenceVersion &&
            expected.TargetScopeKind == (current.PropertyId is null
                ? RetentionTargetScopeKind.Tenant
                : RetentionTargetScopeKind.Property));
    }

    internal static RetentionRunRetryReceiptDto ToReceipt(
        RetentionRunRetryRequest request) => new(
            request.Id,
            request.RunId,
            request.EvidenceVersion,
            request.Attempt,
            request.State switch
            {
                RetentionRunRetryRequestState.Pending =>
                    RetentionRunRetryStatus.Pending,
                RetentionRunRetryRequestState.Applied =>
                    RetentionRunRetryStatus.Applied,
                _ => RetentionRunRetryStatus.Failed
            },
            request.RequestedAtUtc,
            request.ScheduledAtUtc,
            request.CompletedAtUtc,
            request.FailureCode);

    private static RetentionExecutionTargetKind MapTarget(Guid? propertyId) =>
        propertyId is null
            ? RetentionExecutionTargetKind.Tenant
            : RetentionExecutionTargetKind.Property;

    private static Result<RetentionRunRetryReceiptDto> EvidenceChanged() =>
        Result.Failure<RetentionRunRetryReceiptDto>(
            RetentionApplicationErrors.ScheduleRetryEvidenceChanged);

    private static Result<RetentionRunRetryReceiptDto> Unavailable() =>
        Result.Failure<RetentionRunRetryReceiptDto>(
            RetentionApplicationErrors.TaskRunUnavailable);
}
