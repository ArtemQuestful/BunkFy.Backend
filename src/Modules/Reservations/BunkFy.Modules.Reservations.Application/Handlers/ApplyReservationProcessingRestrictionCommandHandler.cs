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

internal sealed class ApplyReservationProcessingRestrictionCommandHandler(
    ReservationMutationCoordinator mutations,
    IReservationProcessingRestrictionProjectionRepository projections,
    IReservationProcessingRestrictionRepository restrictions,
    IReservationArrivalReminderRepository reminders,
    IDataRightsOperationApprovalGate approvalGate,
    IReservationCountryPolicyAdmission countryPolicy,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ApplyReservationProcessingRestrictionCommand,
        ReservationProcessingRestrictionReceiptDto>
{
    public async Task<Result<ReservationProcessingRestrictionReceiptDto>> HandleAsync(
        ApplyReservationProcessingRestrictionCommand command,
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
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.ReservationId == Guid.Empty ||
            command.ExpectedReservationVersion < 1 ||
            command.ExpectedProjectionRevision < 0 ||
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
                DataRightsRestrictionDirective.Apply,
                ExecutingActorId: actorId),
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

        ReservationProcessingRestriction? prior =
            await restrictions.FindByApplyApprovalAsync(
                command.PropertyId,
                command.ReservationId,
                command.CaseId,
                command.ApprovalRevision,
                cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                ReservationsApplicationErrors.ProcessingRestrictionApprovalAlreadyUsed);
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
        Result<ReservationProcessingRestriction> created =
            ReservationProcessingRestriction.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.PropertyId,
                command.ReservationId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedReservationVersion,
                actorId,
                nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                created.Error);
        }

        bool wasRestricted = projection.IsRestricted;
        Result applied = projection.Apply(
            command.ExpectedProjectionRevision,
            ReservationProcessingRestrictionContract.CurrentVersion,
            nowUtc);
        if (applied.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                applied.Error);
        }

        Result<ReservationProcessingRestrictionReceipt> receipt =
            ReservationProcessingRestrictionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                created.Value.Id,
                ReservationProcessingRestrictionAction.Apply,
                command.PropertyId,
                command.ReservationId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedReservationVersion,
                ReservationProcessingRestrictionContract.CurrentVersion,
                created.Value.Version,
                projection.Revision,
                projection.IsRestricted,
                ids.NewId(),
                nowUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                receipt.Error);
        }

        if (!wasRestricted)
        {
            await reminders.SuppressForProcessingRestrictionAsync(
                command.ReservationId,
                cancellationToken).ConfigureAwait(false);
        }

        await restrictions.AddAsync(
            created.Value,
            cancellationToken).ConfigureAwait(false);
        await restrictions.AddReceiptAsync(
            receipt.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private static Result<ReservationProcessingRestrictionReceiptDto> Replay(
        ReservationProcessingRestrictionReceipt receipt,
        ReservationProcessingRestriction? restriction,
        ApplyReservationProcessingRestrictionCommand command,
        string actorId)
    {
        if (receipt.Action != ReservationProcessingRestrictionAction.Apply ||
            restriction is null ||
            restriction.Id != receipt.RestrictionId ||
            !string.Equals(
                restriction.AppliedBy,
                actorId,
                StringComparison.Ordinal) ||
            receipt.PropertyId != command.PropertyId ||
            receipt.ReservationId != command.ReservationId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.SelectedReservationVersion != command.ExpectedReservationVersion ||
            receipt.ContractVersion !=
                ReservationProcessingRestrictionContract.CurrentVersion ||
            receipt.ResultingRestrictionVersion != 1 ||
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
