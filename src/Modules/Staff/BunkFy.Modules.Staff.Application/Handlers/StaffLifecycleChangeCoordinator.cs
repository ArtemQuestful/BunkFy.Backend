namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class StaffLifecycleChangeCoordinator(
    IStaffMemberMutationOperationRepository operations,
    IStaffIdentityProvisioningAnchorResolutionRepository resolutions,
    StaffLifecyclePolicyEvaluator policies,
    ISystemClock clock,
    IIdGenerator ids)
{
    public Task<Result<StaffMemberMutationReceiptDto>> SuspendAsync(
        StaffMember member,
        Guid operationId,
        long expectedVersion,
        StaffLifecycleChangeValues values,
        CancellationToken cancellationToken) => this.ExecuteAsync(
        member,
        operationId,
        expectedVersion,
        values,
        StaffMemberMutationKind.Suspend,
        StaffLifecycleTransition.Suspend,
        StaffStatus.Suspended,
        requestedEffectiveOn: null,
        (eventId, nowUtc) => member.Suspend(
            expectedVersion,
            values.Actor.Value,
            values.Reason.Value,
            eventId,
            nowUtc),
        cancellationToken);

    public Task<Result<StaffMemberMutationReceiptDto>> ResumeAsync(
        StaffMember member,
        Guid operationId,
        long expectedVersion,
        StaffLifecycleChangeValues values,
        CancellationToken cancellationToken) => this.ExecuteAsync(
        member,
        operationId,
        expectedVersion,
        values,
        StaffMemberMutationKind.Resume,
        StaffLifecycleTransition.Resume,
        StaffStatus.Active,
        requestedEffectiveOn: null,
        (eventId, nowUtc) => member.Resume(
            expectedVersion,
            values.Actor.Value,
            values.Reason.Value,
            eventId,
            nowUtc),
        cancellationToken);

    public Task<Result<StaffMemberMutationReceiptDto>> DepartAsync(
        StaffMember member,
        Guid operationId,
        DateOnly effectiveOn,
        long expectedVersion,
        StaffLifecycleChangeValues values,
        CancellationToken cancellationToken) => this.ExecuteAsync(
        member,
        operationId,
        expectedVersion,
        values,
        StaffMemberMutationKind.Depart,
        StaffLifecycleTransition.Depart,
        StaffStatus.Departed,
        effectiveOn,
        (eventId, nowUtc) => member.Depart(
            effectiveOn,
            expectedVersion,
            values.Actor.Value,
            values.Reason.Value,
            eventId,
            member.Assignments
                .Where(assignment => assignment.IsCurrent)
                .Select(_ => ids.NewId())
                .ToArray(),
            nowUtc),
        cancellationToken);

    private async Task<Result<StaffMemberMutationReceiptDto>> ExecuteAsync(
        StaffMember member,
        Guid operationId,
        long expectedVersion,
        StaffLifecycleChangeValues values,
        StaffMemberMutationKind kind,
        StaffLifecycleTransition transition,
        StaffStatus targetStatus,
        DateOnly? requestedEffectiveOn,
        Func<Guid, DateTimeOffset, Result> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(mutate);

        string fingerprint = StaffLifecycleChangeFingerprint.Compute(
            kind,
            member.Id,
            expectedVersion,
            values.Reason,
            requestedEffectiveOn);
        StaffMemberMutationOperationRecord? existing =
            await operations.GetAsync(
                member.Id,
                operationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Matches(
                kind,
                member.Id,
                expectedVersion,
                fingerprint)
                ? Result.Success(existing.ToReceipt())
                : Result.Failure<StaffMemberMutationReceiptDto>(
                    StaffApplicationErrors.LifecycleOperationConflict);
        }

        if (targetStatus == StaffStatus.Active &&
            await resolutions.HasUnresolvedWorkspaceOnboardingAsync(
                member.Id,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<StaffMemberMutationReceiptDto>(
                StaffApplicationErrors.IdentityAnchorResolutionRequired);
        }

        DateTimeOffset nowUtc = StaffMutationTime.Normalize(clock.UtcNow);
        DateOnly effectiveOn = requestedEffectiveOn ??
            DateOnly.FromDateTime(nowUtc.UtcDateTime);
        StaffStatus previousStatus = StaffMappings.MapStatus(member.Status);
        Guid transitionId = ids.NewId();
        Result changed = mutate(transitionId, nowUtc);
        if (changed.IsFailure)
        {
            return Result.Failure<StaffMemberMutationReceiptDto>(changed.Error);
        }

        Result prepared = await policies.PrepareAsync(
            new StaffLifecyclePolicyContext(
                transitionId,
                member.ScopeId,
                member.Id,
                member.AuthSubjectId,
                transition,
                previousStatus,
                targetStatus,
                effectiveOn,
                expectedVersion,
                member.Version,
                values.Actor.Value),
            cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result.Failure<StaffMemberMutationReceiptDto>(
                prepared.Error);
        }

        StaffMemberMutationReceiptDto receipt = new(
            member.Id,
            StaffMappings.MapStatus(member.Status),
            member.Version,
            nowUtc);
        await operations.AddAsync(
            new StaffMemberMutationOperationRecord(
                operationId,
                member.ScopeId,
                member.Id,
                kind,
                expectedVersion,
                fingerprint,
                receipt.Status,
                receipt.Version,
                receipt.CompletedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
