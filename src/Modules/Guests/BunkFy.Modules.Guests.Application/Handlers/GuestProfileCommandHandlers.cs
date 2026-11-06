namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.DataGovernance;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Mapping;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.ValueObjects;

internal sealed class CreateGuestProfileCommandHandler(
    IGuestProfileRepository profiles,
    GuestMutationCoordinator mutations,
    IGuestCountryPolicyAdmission countryPolicy,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids) : ICommandHandler<CreateGuestProfileCommand, GuestMutationReceiptDto>
{
    public async Task<Result<GuestMutationReceiptDto>> HandleAsync(
        CreateGuestProfileCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<GuestMutationReceiptDto>(GuestsApplicationErrors.TenantRequired);
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            command.PropertyId,
            GuestCountryPolicyAdmission.GuestProfileManagementPurpose,
            CountryPolicySurface.ApiWrite,
            GuestCountryPolicyAdmission.AuthorizedOperatorProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            return Result.Failure<GuestMutationReceiptDto>(
                GuestsApplicationErrors.CountryPolicyDenied(policyDecision.Reason));
        }

        DateTimeOffset nowUtc = GuestMutationTime.Normalize(clock.UtcNow);
        Result<GuestProfileCreationSnapshot> creation = GuestProfileCreationSnapshot.Capture(
            command.PropertyId,
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
            command.CreationConfirmationId,
            command.LanguageTags);
        if (creation.IsFailure)
        {
            return Result.Failure<GuestMutationReceiptDto>(creation.Error);
        }

        GuestProfile? existing = await mutations.AcquireCreationAsync(
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.MatchesCreation(creation.Value)
                ? Result.Success(existing.ToMutationReceipt())
                : Result.Failure<GuestMutationReceiptDto>(
                    GuestsApplicationErrors.CreationOperationConflict);
        }

        Result<GuestProfile> created = GuestProfile.Create(
            command.OperationId,
            scopeContext.ScopeId,
            command.PropertyId,
            command.DisplayName,
            command.LegalName,
            command.Email,
            command.Phone,
            command.DateOfBirth,
            command.NationalityCountryCode,
            command.PreferredLanguageTag,
            command.Notes,
            command.ActorId,
            ids.NewId(),
            nowUtc,
            command.CreationConfirmationId,
            command.LanguageTags);
        if (created.IsFailure)
        {
            return Result.Failure<GuestMutationReceiptDto>(created.Error);
        }

        await profiles.AddUnderAcquiredOperationLockAsync(
            created.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(created.Value.ToMutationReceipt());
    }
}

internal sealed class UpdateGuestProfileCommandHandler(
    GuestMutationCoordinator mutations,
    IGuestCountryPolicyAdmission countryPolicy,
    IGuestManagementOperationRepository operations,
    ISystemClock clock,
    IIdGenerator ids) : ICommandHandler<UpdateGuestProfileCommand, GuestMutationReceiptDto>
{
    public async Task<Result<GuestMutationReceiptDto>> HandleAsync(
        UpdateGuestProfileCommand command,
        CancellationToken cancellationToken)
    {
        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            command.PropertyId,
            GuestCountryPolicyAdmission.GuestProfileManagementPurpose,
            CountryPolicySurface.ApiWrite,
            GuestCountryPolicyAdmission.AuthorizedOperatorProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            return Result.Failure<GuestMutationReceiptDto>(
                GuestsApplicationErrors.CountryPolicyDenied(policyDecision.Reason));
        }

        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<GuestMutationReceiptDto>(
                GuestsApplicationErrors.ManagementOperationInvalid);
        }

        DateTimeOffset nowUtc = GuestMutationTime.Normalize(clock.UtcNow);
        Result<GuestProfileChange> values = GuestProfileChange.Create(
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
        if (values.IsFailure)
        {
            return Result.Failure<GuestMutationReceiptDto>(values.Error);
        }

        string fingerprint = GuestManagementOperationFingerprint.Update(
            command.PropertyId,
            command.GuestId,
            command.ExpectedVersion,
            values.Value);
        GuestProfile? profile = await mutations.AcquireVisibleAsync(
            command.PropertyId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Result.Failure<GuestMutationReceiptDto>(GuestsApplicationErrors.GuestNotFound);
        }

        GuestManagementOperationRecord? existing = await operations.GetAsync(
            command.GuestId,
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.MatchesUpdate(
                command.PropertyId,
                command.ExpectedVersion,
                fingerprint)
                ? Result.Success(existing.ToMutationReceipt())
                : Result.Failure<GuestMutationReceiptDto>(
                    GuestsApplicationErrors.ManagementOperationConflict);
        }

        Result updated = profile.UpdateWithOutcome(
            values.Value.DisplayName,
            values.Value.LegalName,
            values.Value.Email,
            values.Value.Phone,
            values.Value.DateOfBirth,
            values.Value.NationalityCountryCode,
            values.Value.PreferredLanguageTag,
            values.Value.Notes,
            command.ExpectedVersion,
            values.Value.ActorId,
            ids.NewId(),
            nowUtc,
            values.Value.LanguageTags);
        if (updated.IsFailure)
        {
            return Result.Failure<GuestMutationReceiptDto>(updated.Error);
        }

        GuestMutationReceiptDto receipt = profile.ToMutationReceipt();
        await operations.AddAsync(
            new(
                command.OperationId,
                profile.ScopeId,
                command.PropertyId,
                command.GuestId,
                GuestManagementOperationKind.Update,
                command.ExpectedVersion,
                fingerprint,
                receipt.Status,
                receipt.Version,
                receipt.LastChangedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}

internal sealed class ArchiveGuestProfileCommandHandler(
    GuestMutationCoordinator mutations,
    IGuestManagementOperationRepository operations,
    ISystemClock clock,
    IIdGenerator ids) : ICommandHandler<ArchiveGuestProfileCommand, GuestMutationReceiptDto>
{
    public async Task<Result<GuestMutationReceiptDto>> HandleAsync(
        ArchiveGuestProfileCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<GuestMutationReceiptDto>(
                GuestsApplicationErrors.ManagementOperationInvalid);
        }

        string normalizedActor = command.ActorId?.Trim() ?? string.Empty;
        if (normalizedActor.Length is 0 or > GuestsContractLimits.ActorIdMaxLength)
        {
            return Result.Failure<GuestMutationReceiptDto>(
                GuestsApplicationErrors.ActorInvalid);
        }

        GuestProfile? profile = await mutations.AcquireVisibleAsync(
            command.PropertyId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Result.Failure<GuestMutationReceiptDto>(GuestsApplicationErrors.GuestNotFound);
        }

        GuestManagementOperationRecord? existing = await operations.GetAsync(
            command.GuestId,
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.MatchesArchive(command.PropertyId, command.ExpectedVersion)
                ? Result.Success(existing.ToMutationReceipt())
                : Result.Failure<GuestMutationReceiptDto>(
                    GuestsApplicationErrors.ManagementOperationConflict);
        }

        DateTimeOffset nowUtc = GuestMutationTime.Normalize(clock.UtcNow);
        Result archived = profile.Archive(
            command.ExpectedVersion,
            normalizedActor,
            ids.NewId(),
            nowUtc);
        if (archived.IsFailure)
        {
            return Result.Failure<GuestMutationReceiptDto>(archived.Error);
        }

        GuestMutationReceiptDto receipt = profile.ToMutationReceipt();
        await operations.AddAsync(
            new(
                command.OperationId,
                profile.ScopeId,
                command.PropertyId,
                command.GuestId,
                GuestManagementOperationKind.Archive,
                command.ExpectedVersion,
                RequestFingerprint: null,
                receipt.Status,
                receipt.Version,
                receipt.LastChangedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
