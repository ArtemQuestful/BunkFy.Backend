namespace BunkFy.Modules.Guests.Domain.Aggregates;

using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class GuestProfile : ScopedAggregateRoot<Guid>
{
    public const int DisplayNameMaxLength = 256;
    public const int LegalNameMaxLength = 256;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 64;
    public const int CountryCodeLength = 2;
    public const int LanguageTagMaxLength = 35;
    public const int NotesMaxLength = 4000;
    public const int ActorIdMaxLength = 200;

    private string[] languageTags = [];

    private GuestProfile() { }

    private GuestProfile(Guid id, string scopeId) : base(id, scopeId) { }

    public Guid OriginPropertyId { get; private set; }
    public Guid? CreationConfirmationId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string DisplayNameSearch { get; private set; } = string.Empty;
    public string? LegalName { get; private set; }
    public string? LegalNameSearch { get; private set; }
    public string? Email { get; private set; }
    public string? EmailSearch { get; private set; }
    public string? Phone { get; private set; }
    public string? PhoneSearch { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public string? NationalityCountryCode { get; private set; }
    public string? PreferredLanguageTag { get; private set; }
    public string[] LanguageTags => [.. this.languageTags];
    public string? Notes { get; private set; }
    public GuestProfileState Status { get; private set; } = GuestProfileState.Active;
    public long Version { get; private set; } = 1;
    public long ProjectionOrdinal { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string LastChangedBy { get; private set; } = string.Empty;
    public DateTimeOffset LastChangedAtUtc { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public DateTimeOffset? AnonymisedAtUtc { get; private set; }

    public static Result<GuestProfile> Create(
        Guid id,
        string tenantId,
        Guid originPropertyId,
        string displayName,
        string? legalName,
        string? email,
        string? phone,
        DateOnly? dateOfBirth,
        string? nationalityCountryCode,
        string? preferredLanguageTag,
        string? notes,
        string actorId,
        Guid eventId,
        DateTimeOffset nowUtc,
        Guid? creationConfirmationId = null,
        string[]? languageTags = null)
    {
        if (id == Guid.Empty)
        {
            return Result.Failure<GuestProfile>(GuestsDomainErrors.GuestIdRequired);
        }

        if (originPropertyId == Guid.Empty)
        {
            return Result.Failure<GuestProfile>(GuestsDomainErrors.PropertyIdRequired);
        }

        if (!TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<GuestProfile>(GuestsDomainErrors.TenantInvalid);
        }

        Result<GuestProfileChange> values = GuestProfileChange.Create(
            displayName,
            legalName,
            email,
            phone,
            dateOfBirth,
            nationalityCountryCode,
            preferredLanguageTag,
            notes,
            actorId,
            nowUtc,
            languageTags);
        if (values.IsFailure)
        {
            return Result.Failure<GuestProfile>(values.Error);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<GuestProfile>(GuestsDomainErrors.EventIdRequired);
        }

        if (creationConfirmationId == Guid.Empty)
        {
            return Result.Failure<GuestProfile>(
                GuestsDomainErrors.CreationConfirmationInvalid);
        }

        GuestProfile profile = new(id, scopeId)
        {
            OriginPropertyId = originPropertyId,
            CreationConfirmationId = creationConfirmationId,
            DisplayName = values.Value.DisplayName,
            DisplayNameSearch = NormalizeRequiredSearch(values.Value.DisplayName),
            LegalName = values.Value.LegalName,
            LegalNameSearch = NormalizeSearch(values.Value.LegalName),
            Email = values.Value.Email,
            EmailSearch = NormalizeSearch(values.Value.Email),
            Phone = values.Value.Phone,
            PhoneSearch = NormalizeSearch(values.Value.Phone),
            DateOfBirth = values.Value.DateOfBirth,
            NationalityCountryCode = values.Value.NationalityCountryCode,
            PreferredLanguageTag = values.Value.PreferredLanguageTag,
            languageTags = GuestLanguageTags.ForCreation(values.Value.LanguageTags, values.Value.PreferredLanguageTag),
            Notes = values.Value.Notes,
            CreatedBy = values.Value.ActorId,
            CreatedAtUtc = nowUtc,
            LastChangedBy = values.Value.ActorId,
            LastChangedAtUtc = nowUtc
        };
        profile.RaiseDomainEvent(new GuestProfileCreatedDomainEvent(
            eventId,
            nowUtc,
            profile.ScopeId,
            profile.Id,
            profile.OriginPropertyId,
            profile.Status,
            profile.Version,
            profile.CreationConfirmationId));
        return Result.Success(profile);
    }

    public Result Update(
        string displayName,
        string? legalName,
        string? email,
        string? phone,
        DateOnly? dateOfBirth,
        string? nationalityCountryCode,
        string? preferredLanguageTag,
        string? notes,
        long expectedVersion,
        string actorId,
        Guid eventId,
        DateTimeOffset nowUtc,
        string[]? languageTags = null)
    {
        Result<GuestProfileUpdateOutcome> updated = this.UpdateWithOutcome(
            displayName,
            legalName,
            email,
            phone,
            dateOfBirth,
            nationalityCountryCode,
            preferredLanguageTag,
            notes,
            expectedVersion,
            actorId,
            eventId,
            nowUtc,
            languageTags);
        return updated.IsSuccess
            ? Result.Success()
            : Result.Failure(updated.Error);
    }

    public Result<GuestProfileUpdateOutcome> UpdateWithOutcome(
        string displayName,
        string? legalName,
        string? email,
        string? phone,
        DateOnly? dateOfBirth,
        string? nationalityCountryCode,
        string? preferredLanguageTag,
        string? notes,
        long expectedVersion,
        string actorId,
        Guid eventId,
        DateTimeOffset nowUtc,
        string[]? languageTags = null)
    {
        Result ready = this.EnsureMutable(expectedVersion, eventId);
        if (ready.IsFailure)
        {
            return Result.Failure<GuestProfileUpdateOutcome>(ready.Error);
        }

        Result<GuestProfileChange> values = GuestProfileChange.Create(
            displayName,
            legalName,
            email,
            phone,
            dateOfBirth,
            nationalityCountryCode,
            preferredLanguageTag,
            notes,
            actorId,
            nowUtc,
            languageTags);
        if (values.IsFailure)
        {
            return Result.Failure<GuestProfileUpdateOutcome>(values.Error);
        }

        return this.ApplyUpdate(values.Value, eventId, nowUtc);
    }

    private Result<GuestProfileUpdateOutcome> ApplyUpdate(
        GuestProfileChange values,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        Result<string[]> resolvedTags = GuestLanguageTags.ResolveUpdate(
            this.languageTags, this.PreferredLanguageTag, values.PreferredLanguageTag, values.LanguageTags);
        if (resolvedTags.IsFailure)
        {
            return Result.Failure<GuestProfileUpdateOutcome>(resolvedTags.Error);
        }

        GuestProfileField[] changedFields = this.GetChangedFields(values, resolvedTags.Value);
        long previousVersion = this.Version;
        this.DisplayName = values.DisplayName;
        this.DisplayNameSearch = NormalizeRequiredSearch(values.DisplayName);
        this.LegalName = values.LegalName;
        this.LegalNameSearch = NormalizeSearch(values.LegalName);
        this.Email = values.Email;
        this.EmailSearch = NormalizeSearch(values.Email);
        this.Phone = values.Phone;
        this.PhoneSearch = NormalizeSearch(values.Phone);
        this.DateOfBirth = values.DateOfBirth;
        this.NationalityCountryCode = values.NationalityCountryCode;
        this.PreferredLanguageTag = values.PreferredLanguageTag;
        this.languageTags = resolvedTags.Value;
        this.Notes = values.Notes;
        this.LastChangedBy = values.ActorId;
        this.LastChangedAtUtc = nowUtc;
        this.Version++;
        this.RaiseDomainEvent(new GuestProfileUpdatedDomainEvent(
            eventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.Status,
            this.Version));
        return Result.Success(new GuestProfileUpdateOutcome(
            previousVersion,
            this.Version,
            eventId,
            nowUtc,
            changedFields));
    }

    public Result Archive(long expectedVersion, string actorId, Guid eventId, DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(GuestsDomainErrors.VersionConflict);
        }

        if (this.Status == GuestProfileState.Archived)
        {
            return Result.Failure(GuestsDomainErrors.GuestAlreadyArchived);
        }

        if (this.Status != GuestProfileState.Active)
        {
            return Result.Failure(GuestsDomainErrors.GuestStatusUnknown);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure(actor.Error);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure(GuestsDomainErrors.EventIdRequired);
        }

        this.Status = GuestProfileState.Archived;
        this.ArchivedAtUtc = nowUtc;
        this.LastChangedBy = actor.Value;
        this.LastChangedAtUtc = nowUtc;
        this.Version++;
        this.RaiseDomainEvent(new GuestProfileArchivedDomainEvent(
            eventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.Version));
        return Result.Success();
    }

    public bool HasValues(GuestProfileChange values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return string.Equals(this.DisplayName, values.DisplayName, StringComparison.Ordinal) &&
               string.Equals(this.LegalName, values.LegalName, StringComparison.Ordinal) &&
               string.Equals(this.Email, values.Email, StringComparison.Ordinal) &&
               string.Equals(this.Phone, values.Phone, StringComparison.Ordinal) &&
               this.DateOfBirth == values.DateOfBirth &&
               string.Equals(
                   this.NationalityCountryCode,
                   values.NationalityCountryCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   this.PreferredLanguageTag,
                   values.PreferredLanguageTag,
                   StringComparison.Ordinal) &&
               string.Equals(this.Notes, values.Notes, StringComparison.Ordinal) &&
               (values.LanguageTags is null || GuestLanguageTags.Equivalent(this.languageTags, values.LanguageTags));
    }

    private Result EnsureMutable(long expectedVersion, Guid eventId)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(GuestsDomainErrors.VersionConflict);
        }

        if (this.Status == GuestProfileState.Archived)
        {
            return Result.Failure(GuestsDomainErrors.GuestArchived);
        }

        if (this.Status != GuestProfileState.Active)
        {
            return Result.Failure(GuestsDomainErrors.GuestStatusUnknown);
        }

        return eventId == Guid.Empty
            ? Result.Failure(GuestsDomainErrors.EventIdRequired)
            : Result.Success();
    }

    private static Result<string> NormalizeActor(string actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ActorIdMaxLength
            ? Result.Success(normalized)
            : Result.Failure<string>(GuestsDomainErrors.ActorInvalid);
    }

    private static string? NormalizeSearch(string? value) => value?.ToUpperInvariant();

    private static string NormalizeRequiredSearch(string value) => value.ToUpperInvariant();

}
