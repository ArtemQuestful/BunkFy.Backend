namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Mapping;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ReleaseReservationProcessingRestrictionCommandHandler(
    ReservationMutationCoordinator mutations,
    IReservationProcessingRestrictionProjectionRepository projections,
    IReservationProcessingRestrictionRepository restrictions,
    IDataRightsOperationApprovalGate approvalGate,
    IReservationCountryPolicyAdmission countryPolicy,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ReleaseReservationProcessingRestrictionCommand,
        ReservationProcessingRestrictionReceiptDto>
{
    public async Task<Result<ReservationProcessingRestrictionReceiptDto>> HandleAsync(
        ReleaseReservationProcessingRestrictionCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.TenantRequired);
        }

        string? actorId = NormalizeActor(command.ActorId);
        if (command.IdempotencyKey == Guid.Empty ||
            command.PropertyId == Guid.Empty ||
            command.RestrictionId == Guid.Empty ||
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.ReservationId == Guid.Empty ||
            command.ExpectedReservationVersion < 1 ||
            command.ExpectedRestrictionVersion < 1 ||
            command.ExpectedProjectionRevision < 1 ||
            actorId is null)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ProcessingRestrictionRequestInvalid);
        }

        ReservationProcessingRestrictionReceipt? existing =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ReservationProcessingRestriction? replayRestriction =
                await restrictions.GetAsync(
                    command.PropertyId,
                    existing.RestrictionId,
                    cancellationToken).ConfigureAwait(false);
            return Replay(existing, replayRestriction, command, actorId);
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            command.PropertyId,
            ReservationCountryPolicyAdmission.DataRightsRestrictionPurpose,
            CountryPolicySurface.ApiWrite,
            ReservationCountryPolicyAdmission.AuthorizedOperatorProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.CountryPolicyDenied(policyDecision.Reason));
        }

        DataRightsOperationApprovalResult approval = await approvalGate.EvaluateAsync(
            new(
                scopeContext.ScopeId,
                command.PropertyId,
                command.CaseId,
                command.ApprovalRevision,
                DataRightsOperation.Restriction,
                ReservationsDataRightsCoordinates.Owner,
                ReservationsDataRightsCoordinates.ReservationRecordType,
                command.ReservationId,
                command.ExpectedReservationVersion,
                DataRightsRestrictionDirective.Release,
                ExecutingActorId: actorId,
                RestrictionTargetOwnerOperationId:
                    command.LegacyUnboundTarget ? null : command.RestrictionId,
                RestrictionTargetOwnerOperationVersion:
                    command.LegacyUnboundTarget
                        ? null
                        : command.ExpectedRestrictionVersion),
            cancellationToken).ConfigureAwait(false);
        if (!approval.IsApproved)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.DataRightsApprovalRequired);
        }

        Reservation? reservation = await mutations.AcquireDataRightsAsync(
            command.PropertyId,
            command.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ReservationNotFound);
        }

        if (reservation.Version != command.ExpectedReservationVersion)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors
                    .ProcessingRestrictionReservationVersionConflict);
        }

        ReservationProcessingRestriction? priorRelease =
            await restrictions.FindByReleaseApprovalAsync(
                command.PropertyId,
                command.ReservationId,
                command.CaseId,
                command.ApprovalRevision,
                cancellationToken).ConfigureAwait(false);
        if (priorRelease is not null)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ProcessingRestrictionApprovalAlreadyUsed);
        }

        ReservationProcessingRestriction? restriction = await restrictions.GetAsync(
            command.PropertyId,
            command.RestrictionId,
            cancellationToken).ConfigureAwait(false);
        if (restriction is null ||
            restriction.ReservationId != command.ReservationId)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ProcessingRestrictionNotFound);
        }

        ReservationProcessingRestrictionProjection? projection =
            await projections.GetAsync(
                command.PropertyId,
                command.ReservationId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null ||
            projection.ContractVersion !=
                ReservationProcessingRestrictionContract.CurrentVersion)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ProcessingRestrictionProjectionUnavailable);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Result restrictionValidation = restriction.ValidateRelease(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedReservationVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        if (restrictionValidation.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                restrictionValidation.Error);
        }

        Result projectionValidation = projection.ValidateRelease(
            command.ExpectedProjectionRevision,
            ReservationProcessingRestrictionContract.CurrentVersion,
            nowUtc);
        if (projectionValidation.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                projectionValidation.Error);
        }

        _ = restriction.Release(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedReservationVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        _ = projection.Release(
            command.ExpectedProjectionRevision,
            ReservationProcessingRestrictionContract.CurrentVersion,
            nowUtc);

        Result<ReservationProcessingRestrictionReceipt> receipt =
            ReservationProcessingRestrictionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                restriction.Id,
                ReservationProcessingRestrictionAction.Release,
                command.PropertyId,
                command.ReservationId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedReservationVersion,
                ReservationProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                projection.Revision,
                projection.IsRestricted,
                ids.NewId(),
                nowUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                receipt.Error);
        }

        await restrictions.AddReceiptAsync(
            receipt.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private static Result<ReservationProcessingRestrictionReceiptDto> Replay(
        ReservationProcessingRestrictionReceipt receipt,
        ReservationProcessingRestriction? restriction,
        ReleaseReservationProcessingRestrictionCommand command,
        string actorId)
    {
        if (receipt.Action != ReservationProcessingRestrictionAction.Release ||
            restriction is null ||
            restriction.Id != receipt.RestrictionId ||
            !string.Equals(
                restriction.ReleasedBy,
                actorId,
                StringComparison.Ordinal) ||
            receipt.RestrictionId != command.RestrictionId ||
            receipt.PropertyId != command.PropertyId ||
            receipt.ReservationId != command.ReservationId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.SelectedReservationVersion != command.ExpectedReservationVersion ||
            receipt.ContractVersion !=
                ReservationProcessingRestrictionContract.CurrentVersion ||
            receipt.ResultingRestrictionVersion - 1 !=
                command.ExpectedRestrictionVersion ||
            receipt.ResultingProjectionRevision - 1 !=
                command.ExpectedProjectionRevision)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors
                    .ProcessingRestrictionIdempotencyConflict);
        }

        return Result.Success(receipt.ToDto());
    }

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= Reservation.ActorIdMaxLength
            ? normalized
            : null;
    }

    private static DateTimeOffset ToPersistencePrecision(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }
}
