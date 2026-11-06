namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class StaffAuthSubjectChangeCoordinator(
    IStaffMemberRepository members,
    IStaffMemberMutationOperationRepository operations,
    IStaffIdentityProvisioningAnchorRepository anchors,
    ISystemClock clock,
    IIdGenerator ids)
{
    public async Task<Result<StaffMemberMutationReceiptDto>> ExecuteAsync(
        StaffMember member,
        Guid operationId,
        long expectedVersion,
        StaffAuthSubjectChangeValues values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(values);
        string fingerprint = StaffAuthSubjectChangeFingerprint.Compute(
            member.Id,
            expectedVersion,
            values.AuthSubject);
        StaffMemberMutationOperationRecord? existing =
            await operations.GetAsync(
                member.Id,
                operationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Matches(
                StaffMemberMutationKind.AuthSubjectChange,
                member.Id,
                expectedVersion,
                fingerprint)
                ? Result.Success(existing.ToReceipt())
                : Result.Failure<StaffMemberMutationReceiptDto>(
                    StaffApplicationErrors.AuthSubjectOperationConflict);
        }

        Result<bool> transition = member.EvaluateAuthSubjectChange(
            values.AuthSubject,
            expectedVersion);
        if (transition.IsFailure)
        {
            return Result.Failure<StaffMemberMutationReceiptDto>(
                transition.Error);
        }

        if (transition.Value)
        {
            if (await anchors.HasWorkspaceOnboardingAsync(
                    member.Id,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<StaffMemberMutationReceiptDto>(
                    StaffApplicationErrors
                        .IdentityAnchorAccessClosureRequired);
            }

            Result uniqueness = await StaffMemberUniqueness.EnsureAuthSubjectAsync(
                members,
                values.AuthSubject.Value,
                member.Id,
                cancellationToken).ConfigureAwait(false);
            if (uniqueness.IsFailure)
            {
                return Result.Failure<StaffMemberMutationReceiptDto>(
                    uniqueness.Error);
            }
        }

        DateTimeOffset nowUtc = StaffMutationTime.Normalize(clock.UtcNow);
        Result changed = member.SetAuthSubject(
            values.AuthSubject,
            expectedVersion,
            values.Actor,
            ids.NewId(),
            nowUtc);
        if (changed.IsFailure)
        {
            return Result.Failure<StaffMemberMutationReceiptDto>(
                changed.Error);
        }

        StaffMemberMutationReceiptDto receipt = new(
            member.Id,
            StaffMappings.MapStatus(member.Status),
            member.Version,
            nowUtc);
        await operations.AddAsync(
            new(
                operationId,
                member.ScopeId,
                member.Id,
                StaffMemberMutationKind.AuthSubjectChange,
                expectedVersion,
                fingerprint,
                receipt.Status,
                receipt.Version,
                receipt.CompletedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
