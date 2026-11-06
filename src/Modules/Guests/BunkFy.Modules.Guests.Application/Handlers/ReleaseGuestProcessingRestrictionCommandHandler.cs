namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Mapping;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ReleaseGuestProcessingRestrictionCommandHandler(
    IGuestProfileRepository profiles,
    IGuestProcessingRestrictionProjectionRepository projections,
    IGuestProcessingRestrictionRepository restrictions,
    IGuestOperationLock operationLock,
    IDataRightsOperationApprovalGate approvalGate,
    IGuestCountryPolicyAdmission countryPolicy,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ReleaseGuestProcessingRestrictionCommand,
        GuestProcessingRestrictionReceiptDto>
{
    public async Task<Result<GuestProcessingRestrictionReceiptDto>> HandleAsync(
        ReleaseGuestProcessingRestrictionCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.TenantRequired);
        }

        string? actorId = NormalizeActor(command.ActorId);
        if (command.IdempotencyKey == Guid.Empty ||
            command.PropertyId == Guid.Empty ||
            command.RestrictionId == Guid.Empty ||
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.GuestId == Guid.Empty ||
            command.ExpectedGuestVersion < 1 ||
            command.ExpectedRestrictionVersion < 1 ||
            command.ExpectedProjectionRevision < 1 ||
            actorId is null)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionRequestInvalid);
        }

        GuestProcessingRestrictionReceipt? existing =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, actorId);
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            command.PropertyId,
            GuestCountryPolicyAdmission.DataRightsRestrictionPurpose,
            CountryPolicySurface.ApiWrite,
            GuestCountryPolicyAdmission.AuthorizedOperatorProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.CountryPolicyDenied(policyDecision.Reason));
        }

        DataRightsOperationApprovalResult approval = await approvalGate.EvaluateAsync(
            new(
                scopeContext.ScopeId,
                command.PropertyId,
                command.CaseId,
                command.ApprovalRevision,
                DataRightsOperation.Restriction,
                GuestsDataRightsCoordinates.Owner,
                GuestsDataRightsCoordinates.GuestProfileRecordType,
                command.GuestId,
                command.ExpectedGuestVersion,
                DataRightsRestrictionDirective.Release,
                ExecutingActorId: actorId,
                CaseType: DataRightsCaseType.GuestRights,
                RestrictionTargetOwnerOperationId:
                    command.LegacyUnboundTarget ? null : command.RestrictionId,
                RestrictionTargetOwnerOperationVersion:
                    command.LegacyUnboundTarget
                        ? null
                        : command.ExpectedRestrictionVersion),
            cancellationToken).ConfigureAwait(false);
        if (!approval.IsApproved)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.DataRightsApprovalRequired);
        }

        await operationLock.AcquireGuestAsync(
            scopeContext.ScopeId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);

        GuestProfile? profile = await profiles.GetForDataRightsAsync(
            command.PropertyId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.GuestNotFound);
        }

        if (profile.Version != command.ExpectedGuestVersion)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionGuestVersionConflict);
        }

        GuestProcessingRestriction? priorRelease =
            await restrictions.FindByReleaseApprovalAsync(
                command.PropertyId,
                command.GuestId,
                command.CaseId,
                command.ApprovalRevision,
                cancellationToken).ConfigureAwait(false);
        if (priorRelease is not null)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionApprovalAlreadyUsed);
        }

        GuestProcessingRestriction? restriction = await restrictions.GetAsync(
            command.PropertyId,
            command.RestrictionId,
            cancellationToken).ConfigureAwait(false);
        if (restriction is null || restriction.GuestId != command.GuestId)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionNotFound);
        }

        GuestProcessingRestrictionProjection? projection = await projections.GetAsync(
            command.PropertyId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (projection is null ||
            projection.ContractVersion != GuestProcessingRestrictionContract.CurrentVersion)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionProjectionUnavailable);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Result restrictionValidation = restriction.ValidateRelease(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedGuestVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        if (restrictionValidation.IsFailure)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                restrictionValidation.Error);
        }

        Result projectionValidation = projection.ValidateRelease(
            command.ExpectedProjectionRevision,
            GuestProcessingRestrictionContract.CurrentVersion,
            nowUtc);
        if (projectionValidation.IsFailure)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                projectionValidation.Error);
        }

        _ = restriction.Release(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedGuestVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        _ = projection.Release(
            command.ExpectedProjectionRevision,
            GuestProcessingRestrictionContract.CurrentVersion,
            nowUtc);

        Result<GuestProcessingRestrictionReceipt> receipt =
            GuestProcessingRestrictionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                restriction.Id,
                GuestProcessingRestrictionAction.Release,
                command.PropertyId,
                command.GuestId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedGuestVersion,
                GuestProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                projection.Revision,
                projection.IsRestricted,
                actorId,
                ids.NewId(),
                nowUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(receipt.Error);
        }

        await restrictions.AddReceiptAsync(receipt.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private static Result<GuestProcessingRestrictionReceiptDto> Replay(
        GuestProcessingRestrictionReceipt receipt,
        ReleaseGuestProcessingRestrictionCommand command,
        string actorId)
    {
        if (receipt.Action != GuestProcessingRestrictionAction.Release ||
            receipt.RestrictionId != command.RestrictionId ||
            receipt.PropertyId != command.PropertyId ||
            receipt.GuestId != command.GuestId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.SelectedGuestVersion != command.ExpectedGuestVersion ||
            receipt.ResultingRestrictionVersion - 1 != command.ExpectedRestrictionVersion ||
            receipt.ResultingProjectionRevision - 1 != command.ExpectedProjectionRevision ||
            !string.Equals(receipt.ActorId, actorId, StringComparison.Ordinal))
        {
            return Result.Failure<GuestProcessingRestrictionReceiptDto>(
                GuestsApplicationErrors.RestrictionIdempotencyConflict);
        }

        return Result.Success(receipt.ToDto());
    }

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= GuestProfile.ActorIdMaxLength
            ? normalized
            : null;
    }

    private static DateTimeOffset ToPersistencePrecision(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return new(value.Ticks - (value.Ticks % ticksPerMicrosecond), value.Offset);
    }
}
