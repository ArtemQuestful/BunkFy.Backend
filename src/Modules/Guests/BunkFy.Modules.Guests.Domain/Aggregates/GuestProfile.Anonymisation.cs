namespace BunkFy.Modules.Guests.Domain.Aggregates;

using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Results;

public sealed partial class GuestProfile
{
    public const string AnonymisedDisplayName = "Anonymised guest";

    public static Result<GuestProfile> RestoreMissingAnonymised(
        Guid id,
        string tenantId,
        Guid originPropertyId,
        string actorId,
        Guid eventId,
        DateTimeOffset originallyCompletedAtUtc,
        DateTimeOffset replayedAtUtc)
    {
        Result<GuestProfile> created = Create(
            id,
            tenantId,
            originPropertyId,
            AnonymisedDisplayName,
            legalName: null,
            email: null,
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            actorId,
            eventId,
            originallyCompletedAtUtc.ToUniversalTime());
        if (created.IsFailure)
        {
            return created;
        }

        created.Value.ClearDomainEvents();
        Result<GuestProfileAnonymisationOutcome> anonymised =
            created.Value.RestoreAnonymisation(
                actorId,
                eventId,
                originallyCompletedAtUtc,
                replayedAtUtc);
        return anonymised.IsFailure
            ? Result.Failure<GuestProfile>(anonymised.Error)
            : Result.Success(created.Value);
    }

    public Result<GuestProfileAnonymisationOutcome> Anonymise(
        long expectedVersion,
        string actorId,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.VersionConflict);
        }

        if (this.Status == GuestProfileState.Anonymised)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.GuestAlreadyAnonymised);
        }

        if (this.Status != GuestProfileState.Active)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.GuestNotActiveForAnonymisation);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(actor.Error);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.EventIdRequired);
        }

        if (nowUtc == default)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.AnonymisationTimestampInvalid);
        }

        return this.ApplyAnonymisedState(
            actor.Value,
            eventId,
            nowUtc,
            nowUtc);
    }

    public Result<GuestProfileAnonymisationOutcome> AnonymiseForRetention(
        long expectedVersion,
        string actorId,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.VersionConflict);
        }

        if (this.Status == GuestProfileState.Anonymised)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.GuestAlreadyAnonymised);
        }

        if (this.Status is not (
                GuestProfileState.Active or
                GuestProfileState.Archived))
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.GuestNotRetainable);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                actor.Error);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.EventIdRequired);
        }

        if (nowUtc == default)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.AnonymisationTimestampInvalid);
        }

        return this.ApplyAnonymisedState(
            actor.Value,
            eventId,
            nowUtc,
            nowUtc);
    }

    public Result<GuestProfileAnonymisationOutcome> RestoreAnonymisation(
        string actorId,
        Guid eventId,
        DateTimeOffset originallyCompletedAtUtc,
        DateTimeOffset replayedAtUtc)
    {
        if (this.Status is not (
                GuestProfileState.Active or
                GuestProfileState.Archived))
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.AnonymisationRestoreTransitionInvalid);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                actor.Error);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.EventIdRequired);
        }

        DateTimeOffset completedAtUtc =
            originallyCompletedAtUtc.ToUniversalTime();
        DateTimeOffset occurredAtUtc = replayedAtUtc.ToUniversalTime();
        if (completedAtUtc == default ||
            occurredAtUtc == default ||
            occurredAtUtc < completedAtUtc)
        {
            return Result.Failure<GuestProfileAnonymisationOutcome>(
                GuestsDomainErrors.AnonymisationTimestampInvalid);
        }

        return this.ApplyAnonymisedState(
            actor.Value,
            eventId,
            completedAtUtc,
            occurredAtUtc);
    }

    public bool MatchesAnonymisedState(DateTimeOffset completedAtUtc) =>
        this.Status == GuestProfileState.Anonymised &&
        this.AnonymisedAtUtc == completedAtUtc.ToUniversalTime() &&
        this.HasScrubbedPersonalData();

    public bool MatchesAnonymisedState(long version, DateTimeOffset completedAtUtc) =>
        this.Version == version &&
        this.MatchesAnonymisedState(completedAtUtc);

    private Result<GuestProfileAnonymisationOutcome> ApplyAnonymisedState(
        string actorId,
        Guid eventId,
        DateTimeOffset completedAtUtc,
        DateTimeOffset occurredAtUtc)
    {
        long previousVersion = this.Version;
        this.DisplayName = AnonymisedDisplayName;
        this.DisplayNameSearch = NormalizeRequiredSearch(AnonymisedDisplayName);
        this.LegalName = null;
        this.LegalNameSearch = null;
        this.Email = null;
        this.EmailSearch = null;
        this.Phone = null;
        this.PhoneSearch = null;
        this.DateOfBirth = null;
        this.NationalityCountryCode = null;
        this.PreferredLanguageTag = null;
        this.languageTags = [];
        this.Notes = null;
        this.CreationConfirmationId = null;
        this.Status = GuestProfileState.Anonymised;
        this.ArchivedAtUtc = null;
        this.AnonymisedAtUtc = completedAtUtc;
        this.LastChangedBy = actorId;
        this.LastChangedAtUtc = occurredAtUtc;
        this.Version++;
        this.RaiseDomainEvent(new GuestProfileAnonymisedDomainEvent(
            eventId,
            occurredAtUtc,
            this.ScopeId,
            this.Id,
            this.Version));
        return Result.Success(new GuestProfileAnonymisationOutcome(
            previousVersion,
            this.Version,
            eventId,
            occurredAtUtc));
    }

    private bool HasScrubbedPersonalData() =>
        string.Equals(this.DisplayName, AnonymisedDisplayName, StringComparison.Ordinal) &&
        string.Equals(
            this.DisplayNameSearch,
            NormalizeRequiredSearch(AnonymisedDisplayName),
            StringComparison.Ordinal) &&
        this.LegalName is null &&
        this.LegalNameSearch is null &&
        this.Email is null &&
        this.EmailSearch is null &&
        this.Phone is null &&
        this.PhoneSearch is null &&
        this.DateOfBirth is null &&
        this.NationalityCountryCode is null &&
        this.PreferredLanguageTag is null &&
        this.languageTags.Length == 0 &&
        this.Notes is null &&
        this.CreationConfirmationId is null &&
        this.ArchivedAtUtc is null;
}
