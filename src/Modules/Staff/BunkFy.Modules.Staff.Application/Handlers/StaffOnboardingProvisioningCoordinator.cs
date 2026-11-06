namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class StaffOnboardingProvisioningCoordinator(
    IStaffMemberRepository members,
    IStaffIdentityProvisioningAnchorRepository anchors,
    IStaffIdentityProvisioningAnchorWriter anchorWriter,
    IStaffOnboardingProvisioningOperationRepository operations,
    IStaffCreationOperationLock operationLock,
    StaffMemberMutationCoordinator mutations,
    ISystemClock clock,
    IIdGenerator ids)
{
    public async Task<Result<StaffMemberDto>> ExecuteAsync(
        ProvisionStaffOnboardingCommand command,
        string scopeId,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<StaffMemberDto>(
                StaffApplicationErrors.OnboardingOperationInvalid);
        }

        Result<StaffProfile> profile = StaffProfile.Create(
            command.DisplayName,
            command.LegalName,
            command.WorkEmail,
            command.WorkPhone,
            command.EmployeeNumber,
            command.JobTitle,
            command.Department,
            command.AuthSubjectId);
        if (profile.IsFailure)
        {
            return Result.Failure<StaffMemberDto>(profile.Error);
        }

        if (profile.Value.AuthSubjectId is null)
        {
            return Result.Failure<StaffMemberDto>(
                StaffApplicationErrors.OnboardingApplicantInvalid);
        }

        Result<StaffActorId> actor = StaffActorId.Create(command.ActorId);
        if (actor.IsFailure)
        {
            return Result.Failure<StaffMemberDto>(actor.Error);
        }

        string fingerprint = StaffOnboardingProvisioningFingerprint.Compute(
            profile.Value);
        await operationLock.AcquireAsync(
                scopeId,
                command.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        StaffIdentityProvisioningAnchorRecord? existingAnchor =
            await anchors.GetAsync(
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                command.OperationId,
                cancellationToken).ConfigureAwait(false);
        StaffMemberMutationOperationRecord? existingOperation =
            await operations.GetAsync(
                command.OperationId,
                cancellationToken).ConfigureAwait(false);
        if (existingAnchor is not null || existingOperation is not null)
        {
            if (existingAnchor is null ||
                existingOperation is null ||
                !existingAnchor.ResolutionEventId.HasValue ||
                existingAnchor.StaffMemberId !=
                    existingOperation.StaffMemberId)
            {
                return Result.Failure<StaffMemberDto>(
                    StaffApplicationErrors.OnboardingReplayUnavailable);
            }

            return await this.ReplayAsync(
                existingOperation,
                profile.Value,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
        }

        StaffMember? member = await this.ResolveMemberAsync(
            profile.Value.AuthSubjectId,
            cancellationToken).ConfigureAwait(false);
        if (member is not null &&
            !string.Equals(
                member.AuthSubjectId,
                profile.Value.AuthSubjectId,
                StringComparison.Ordinal))
        {
            return Result.Failure<StaffMemberDto>(
                StaffApplicationErrors.StaffMemberNotFound);
        }

        Result unique = await StaffMemberUniqueness.EnsureAsync(
            members,
            profile.Value.EmployeeNumber,
            profile.Value.AuthSubjectId,
            member?.Id,
            cancellationToken).ConfigureAwait(false);
        if (unique.IsFailure)
        {
            return Result.Failure<StaffMemberDto>(unique.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<StaffOnboardingProvisioningMutation> provisioned =
            member is null
                ? await this.CreateAsync(
                    profile.Value,
                    actor.Value,
                    scopeId,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false)
                : this.UpdateExisting(
                    member,
                    profile.Value,
                    actor.Value,
                    nowUtc);
        if (provisioned.IsFailure)
        {
            return Result.Failure<StaffMemberDto>(provisioned.Error);
        }

        StaffOnboardingProvisioningMutation mutation = provisioned.Value;
        await operations.AddAsync(
            new StaffMemberMutationOperationRecord(
                command.OperationId,
                mutation.Member.ScopeId,
                mutation.Member.Id,
                StaffMemberMutationKind.OnboardingProvision,
                mutation.StartingVersion,
                fingerprint,
                StaffStatus.Active,
                mutation.Member.Version,
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        await anchorWriter.AddAsync(
            new StaffIdentityProvisioningAnchorRecord(
                mutation.Member.ScopeId,
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                command.OperationId,
                mutation.Member.Id,
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(mutation.Member.ToDto());
    }

    private async Task<StaffMember?> ResolveMemberAsync(
        string authSubjectId,
        CancellationToken cancellationToken)
    {
        StaffMember? member = await members.GetByAuthSubjectAsync(
            authSubjectId,
            cancellationToken).ConfigureAwait(false);
        return member is null
            ? null
            : await mutations.AcquireOperationalAsync(
                member.Id,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<StaffOnboardingProvisioningMutation>> CreateAsync(
        StaffProfile profile,
        StaffActorId actor,
        string scopeId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Result<StaffMember> created = StaffMember.Create(
            ids.NewId(),
            scopeId,
            profile.DisplayName,
            profile.LegalName,
            profile.WorkEmail,
            profile.WorkPhone,
            profile.EmployeeNumber,
            profile.JobTitle,
            profile.Department,
            profile.AuthSubjectId,
            actor.Value,
            ids.NewId(),
            nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<StaffOnboardingProvisioningMutation>(
                created.Error);
        }

        await members.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(new StaffOnboardingProvisioningMutation(
            created.Value,
            created.Value.Version));
    }

    private Result<StaffOnboardingProvisioningMutation> UpdateExisting(
        StaffMember member,
        StaffProfile profile,
        StaffActorId actor,
        DateTimeOffset nowUtc)
    {
        if (member.Status == StaffMemberState.Departed)
        {
            return Result.Failure<StaffOnboardingProvisioningMutation>(
                StaffApplicationErrors.StaffDeparted);
        }

        if (member.Status == StaffMemberState.Suspended)
        {
            return Result.Failure<StaffOnboardingProvisioningMutation>(
                StaffApplicationErrors.StaffSuspended);
        }

        long startingVersion = member.Version;
        Result updated = member.UpdateProfile(
            profile.DisplayName,
            profile.LegalName,
            profile.WorkEmail,
            profile.WorkPhone,
            profile.EmployeeNumber,
            profile.JobTitle,
            profile.Department,
            member.Version,
            actor.Value,
            ids.NewId(),
            nowUtc);
        if (updated.IsFailure)
        {
            return Result.Failure<StaffOnboardingProvisioningMutation>(
                updated.Error);
        }

        return Result.Success(new StaffOnboardingProvisioningMutation(
            member,
            startingVersion));
    }

    private async Task<Result<StaffMemberDto>> ReplayAsync(
        StaffMemberMutationOperationRecord operation,
        StaffProfile profile,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!operation.MatchesOnboarding(fingerprint))
        {
            return Result.Failure<StaffMemberDto>(
                StaffApplicationErrors.OnboardingOperationConflict);
        }

        StaffMember? member = await mutations.AcquireOperationalAsync(
            operation.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (member is null ||
            member.Status != StaffMemberState.Active ||
            !string.Equals(
                member.AuthSubjectId,
                profile.AuthSubjectId,
                StringComparison.Ordinal))
        {
            return Result.Failure<StaffMemberDto>(
                StaffApplicationErrors.OnboardingReplayUnavailable);
        }

        return Result.Success(member.ToDto());
    }

    private sealed record StaffOnboardingProvisioningMutation(
        StaffMember Member,
        long StartingVersion);
}
