namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Errors;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class BootstrapStaffIdentityCommandHandler(
    IStaffMemberRepository members,
    IStaffIdentityProvisioningAnchorRepository anchors,
    IStaffCreationOperationLock creationLock,
    StaffMemberMutationCoordinator mutations,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids) : ICommandHandler<BootstrapStaffIdentityCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        BootstrapStaffIdentityCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<Unit>(StaffApplicationErrors.TenantRequired);
        }

        string scopeId = scopeContext.ScopeId;

        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<Unit>(
                StaffApplicationErrors.CreationOperationInvalid);
        }

        if (command.SourceId == Guid.Empty)
        {
            return Result.Failure<Unit>(
                StaffApplicationErrors.IdentityProvisioningSourceInvalid);
        }

        Result<StaffAuthSubject> authSubject = StaffAuthSubject.Create(
            command.AuthSubjectId);
        if (authSubject.IsFailure || authSubject.Value.Value is null)
        {
            return Result.Failure<Unit>(StaffDomainErrors.AuthSubjectInvalid);
        }

        string normalizedAuthSubject = authSubject.Value.Value;

        await creationLock.AcquireAsync(
                scopeId,
                command.SourceId,
                cancellationToken)
            .ConfigureAwait(false);
        StaffIdentityProvisioningAnchorRecord? existingAnchor =
            await anchors.GetAsync(
                StaffIdentityProvisioningSourceKind.OrganizationMembership,
                command.SourceId,
                cancellationToken).ConfigureAwait(false);
        if (existingAnchor is not null)
        {
            return await this.ValidateExistingAnchorAsync(
                existingAnchor,
                normalizedAuthSubject,
                cancellationToken).ConfigureAwait(false);
        }

        StaffMember? operationOwner =
            await mutations.AcquireSafetyTransitionAsync(
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (operationOwner is not null)
        {
            if (operationOwner.AuthSubjectId is not null &&
                !string.Equals(
                    operationOwner.AuthSubjectId,
                    normalizedAuthSubject,
                    StringComparison.Ordinal))
            {
                return Result.Failure<Unit>(
                    StaffApplicationErrors.CreationOperationConflict);
            }

            await this.AddAnchorAsync(
                scopeId,
                command.SourceId,
                operationOwner.Id,
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            return Result.Success(Unit.Value);
        }

        StaffMember? subjectOwner =
            await members.GetForSafetyTransitionByAuthSubjectAsync(
                normalizedAuthSubject,
                cancellationToken).ConfigureAwait(false);
        if (subjectOwner is not null)
        {
            subjectOwner = await mutations.AcquireSafetyTransitionAsync(
                subjectOwner.Id,
                cancellationToken).ConfigureAwait(false);
            if (subjectOwner is null ||
                !string.Equals(
                    subjectOwner.AuthSubjectId,
                    normalizedAuthSubject,
                    StringComparison.Ordinal))
            {
                return Result.Failure<Unit>(
                    StaffApplicationErrors.CreationOperationConflict);
            }

            await this.AddAnchorAsync(
                scopeId,
                command.SourceId,
                subjectOwner.Id,
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            return Result.Success(Unit.Value);
        }

        Result<StaffProfile> profile = StaffProfile.Create(
            command.DisplayName,
            legalName: null,
            command.WorkEmail,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            normalizedAuthSubject);
        if (profile.IsFailure)
        {
            return Result.Failure<Unit>(profile.Error);
        }

        Result<StaffActorId> actor = StaffActorId.Create(command.ActorId);
        if (actor.IsFailure)
        {
            return Result.Failure<Unit>(actor.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<StaffMember> created = StaffMember.Create(
            command.OperationId,
            scopeId,
            profile.Value.DisplayName,
            profile.Value.LegalName,
            profile.Value.WorkEmail,
            profile.Value.WorkPhone,
            profile.Value.EmployeeNumber,
            profile.Value.JobTitle,
            profile.Value.Department,
            profile.Value.AuthSubjectId,
            actor.Value.Value,
            ids.NewId(),
            nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<Unit>(created.Error);
        }

        await members.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        await this.AddAnchorAsync(
            scopeId,
            command.SourceId,
            created.Value.Id,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ValidateExistingAnchorAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        string normalizedAuthSubject,
        CancellationToken cancellationToken)
    {
        StaffMember? target = await mutations.AcquireSafetyTransitionAsync(
            anchor.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Result.Failure<Unit>(
                StaffApplicationErrors.IdentityProvisioningAnchorCorrupt);
        }

        return target.AuthSubjectId is null ||
            string.Equals(
                target.AuthSubjectId,
                normalizedAuthSubject,
                StringComparison.Ordinal)
            ? Result.Success(Unit.Value)
            : Result.Failure<Unit>(
                StaffApplicationErrors.IdentityProvisioningAnchorConflict);
    }

    private Task AddAnchorAsync(
        string scopeId,
        Guid sourceId,
        Guid staffMemberId,
        DateTimeOffset anchoredAtUtc,
        CancellationToken cancellationToken) =>
        anchors.AddAsync(
            new StaffIdentityProvisioningAnchorRecord(
                scopeId,
                StaffIdentityProvisioningSourceKind.OrganizationMembership,
                sourceId,
                staffMemberId,
                anchoredAtUtc),
            cancellationToken);
}
