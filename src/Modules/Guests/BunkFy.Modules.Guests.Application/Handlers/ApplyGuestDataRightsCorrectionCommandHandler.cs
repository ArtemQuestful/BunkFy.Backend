namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Mapping;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ApplyGuestDataRightsCorrectionCommandHandler(
    IGuestProfileRepository profiles,
    IGuestDataRightsCorrectionReceiptRepository receipts,
    IGuestOperationLock operationLock,
    IDataRightsCorrectionExecutionGate executionGate,
    IGuestCountryPolicyAdmission countryPolicy,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<ApplyGuestDataRightsCorrectionCommand, GuestDataRightsCorrectionReceiptDto>
{
    public async Task<Result<GuestDataRightsCorrectionReceiptDto>> HandleAsync(
        ApplyGuestDataRightsCorrectionCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.TenantRequired);
        }

        if (command.IdempotencyKey == Guid.Empty ||
            command.PropertyId == Guid.Empty ||
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.GuestId == Guid.Empty ||
            command.ExpectedVersion < 1)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.CorrectionRequestInvalid);
        }

        GuestDataRightsCorrectionReceipt? existing =
            await receipts.FindByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await this.ReplayAsync(existing, command, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Result<GuestProfileChange> requested = CreateChange(command, nowUtc);
        if (requested.IsFailure)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(requested.Error);
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            command.PropertyId,
            GuestCountryPolicyAdmission.DataRightsCorrectionPurpose,
            CountryPolicySurface.ApiWrite,
            GuestCountryPolicyAdmission.AuthorizedOperatorProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.CountryPolicyDenied(policyDecision.Reason));
        }

        DataRightsCorrectionExecutionGateResult execution =
            await executionGate.EvaluateAsync(
            new(
                scopeContext.ScopeId,
                DataRightsCaseType.GuestRights,
                command.PropertyId,
                command.CaseId,
                command.ApprovalRevision,
                command.IdempotencyKey,
                new DataRightsSubjectCoordinate(
                    GuestsDataRightsCoordinates.Owner,
                    GuestsDataRightsCoordinates.GuestProfileRecordType,
                    command.GuestId,
                    command.ExpectedVersion),
                GuestsDataRightsCoordinates.CorrectionFieldPolicyKey,
                command.ActorId),
            cancellationToken).ConfigureAwait(false);
        if (!execution.IsAllowed)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
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
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.GuestNotFound);
        }

        if (profile.HasValues(requested.Value))
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.CorrectionNoChanges);
        }

        Guid eventId = ids.NewId();
        Result<GuestProfileUpdateOutcome> updated = profile.UpdateWithOutcome(
            requested.Value.DisplayName,
            requested.Value.LegalName,
            requested.Value.Email,
            requested.Value.Phone,
            requested.Value.DateOfBirth,
            requested.Value.NationalityCountryCode,
            requested.Value.PreferredLanguageTag,
            requested.Value.Notes,
            command.ExpectedVersion,
            requested.Value.ActorId,
            eventId,
            nowUtc,
            requested.Value.LanguageTags);
        if (updated.IsFailure)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(updated.Error);
        }

        Result<GuestDataRightsCorrectionReceipt> created =
            GuestDataRightsCorrectionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                command.PropertyId,
                command.CaseId,
                command.ApprovalRevision,
                command.GuestId,
                updated.Value.PreviousVersion,
                updated.Value.CurrentVersion,
                updated.Value.ChangedFields,
                updated.Value.EventId,
                ids.NewId(),
                updated.Value.OccurredAtUtc);
        if (created.IsFailure)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(created.Error);
        }

        await receipts.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(created.Value.ToDto());
    }

    private async Task<Result<GuestDataRightsCorrectionReceiptDto>> ReplayAsync(
        GuestDataRightsCorrectionReceipt receipt,
        ApplyGuestDataRightsCorrectionCommand command,
        CancellationToken cancellationToken)
    {
        if (receipt.PropertyId != command.PropertyId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.GuestId != command.GuestId ||
            receipt.SelectedRecordVersion != command.ExpectedVersion)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.CorrectionIdempotencyConflict);
        }

        Result<GuestProfileChange> requested = CreateChange(command, clock.UtcNow);
        if (requested.IsFailure)
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(requested.Error);
        }

        GuestProfile? profile = await profiles.GetForDataRightsAsync(
            command.PropertyId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null ||
            profile.Version != receipt.CurrentRecordVersion ||
            !profile.HasValues(requested.Value))
        {
            return Result.Failure<GuestDataRightsCorrectionReceiptDto>(
                GuestsApplicationErrors.CorrectionIdempotencyConflict);
        }

        return Result.Success(receipt.ToDto());
    }

    private static Result<GuestProfileChange> CreateChange(
        ApplyGuestDataRightsCorrectionCommand command,
        DateTimeOffset nowUtc) => GuestProfileChange.Create(
        command.DisplayName,
        command.LegalName,
        command.Email,
        command.Phone,
        command.DateOfBirth,
        command.NationalityCountryCode,
        command.PreferredLanguageTag,
        command.Notes,
        command.ActorId,
        nowUtc,
        command.LanguageTags);

    private static DateTimeOffset ToPersistencePrecision(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }
}
