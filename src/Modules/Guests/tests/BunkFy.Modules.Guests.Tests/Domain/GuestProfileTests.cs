namespace BunkFy.Modules.Guests.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.TimeZones;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_normalizes_profile_and_actor_without_treating_contact_as_identity()
    {
        GuestProfile first = Create("ADA Guest", "Shared@Example.Test", "+1 555 0100");
        GuestProfile second = Create("Second Guest", "Shared@Example.Test", "+1 555 0100");

        Assert.Equal("ADA Guest", first.DisplayName);
        Assert.Equal("ADA GUEST", first.DisplayNameSearch);
        Assert.Equal("shared@example.test", first.Email);
        Assert.Equal("SHARED@EXAMPLE.TEST", first.EmailSearch);
        Assert.Equal("US", first.NationalityCountryCode);
        Assert.Equal("user:operator-a", first.CreatedBy);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(GuestProfileState.Active, first.Status);
        Assert.Equal(1, first.Version);
    }

    [Fact]
    public void Creation_confirmation_is_part_of_exact_replay_identity()
    {
        Guid confirmationId = Guid.NewGuid();
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "Ada Guest",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "user:operator-a",
            Guid.NewGuid(),
            Now,
            confirmationId).Value;
        GuestProfileCreationSnapshot same = GuestProfileCreationSnapshot.Capture(
            profile.OriginPropertyId,
            profile.DisplayName,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            profile.Notes,
            profile.CreatedBy,
            profile.CreatedAtUtc,
            confirmationId).Value;
        GuestProfileCreationSnapshot different = GuestProfileCreationSnapshot.Capture(
            profile.OriginPropertyId,
            profile.DisplayName,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            profile.Notes,
            profile.CreatedBy,
            profile.CreatedAtUtc,
            Guid.NewGuid()).Value;

        Assert.Equal(confirmationId, profile.CreationConfirmationId);
        Assert.True(profile.MatchesCreation(same));
        Assert.False(profile.MatchesCreation(different));
    }

    [Fact]
    public void Update_and_archive_are_versioned_and_archived_profiles_are_immutable()
    {
        GuestProfile profile = Create("Ada Guest", null, null);

        Assert.True(profile.Update(
            "Ada Lovelace",
            "Augusta Ada King",
            "ada@example.test",
            null,
            new DateOnly(1815, 12, 10),
            "gb",
            "en-GB",
            "Returning guest",
            expectedVersion: 1,
            "user:operator-b",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(2, profile.Version);
        Assert.Equal("user:operator-b", profile.LastChangedBy);

        Assert.Equal(
            "Guests.VersionConflict",
            profile.Archive(1, "user:operator-b", Guid.NewGuid(), Now.AddMinutes(2)).Error.Code);
        Assert.True(profile.Archive(
            2, "user:operator-b", Guid.NewGuid(), Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(GuestProfileState.Archived, profile.Status);
        Assert.Equal(3, profile.Version);
        Assert.Equal(
            "Guests.GuestArchived",
            profile.Update(
                profile.DisplayName,
                profile.LegalName,
                profile.Email,
                profile.Phone,
                profile.DateOfBirth,
                profile.NationalityCountryCode,
                profile.PreferredLanguageTag,
                profile.Notes,
                profile.Version,
                "user:operator-c",
                Guid.NewGuid(),
                Now.AddMinutes(3)).Error.Code);
    }

    [Fact]
    public void Future_birth_date_and_invalid_provenance_are_rejected()
    {
        Assert.Equal(
            "Guests.DateOfBirthInvalid",
            GuestProfile.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                "Future Guest",
                null,
                null,
                null,
                new DateOnly(2027, 1, 1),
                null,
                null,
                null,
                "user:operator-a",
                Guid.NewGuid(),
                Now).Error.Code);
        Assert.Equal(
            "Guests.ActorInvalid",
            GuestProfile.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                "Guest",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                " ",
                Guid.NewGuid(),
                Now).Error.Code);
    }

    [Fact]
    public void Update_outcome_reports_normalized_changed_fields_without_personal_values()
    {
        GuestProfile profile = Create("Ada Guest", "ada@example.test", "+1 555 0100");
        Guid eventId = Guid.NewGuid();
        DateTimeOffset changedAtUtc = Now.AddMinutes(1);

        Result<GuestProfileUpdateOutcome> result = profile.UpdateWithOutcome(
            "  Ada Guest  ",
            null,
            "NEW@example.test",
            "  +1 555 0100  ",
            profile.DateOfBirth,
            "us",
            "en-US",
            "Returning guest",
            profile.Version,
            "user:operator-b",
            eventId,
            changedAtUtc);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PreviousVersion);
        Assert.Equal(2, result.Value.CurrentVersion);
        Assert.Equal(eventId, result.Value.EventId);
        Assert.Equal(changedAtUtc, result.Value.OccurredAtUtc);
        Assert.Equal(
            [GuestProfileField.Email, GuestProfileField.Notes],
            result.Value.ChangedFields);
        Assert.Equal("new@example.test", profile.Email);
        Assert.DoesNotContain(
            typeof(GuestProfileUpdateOutcome).GetProperties(),
            property => property.Name is
                "DisplayName" or
                "LegalName" or
                "Email" or
                "Phone" or
                "DateOfBirth" or
                "NationalityCountryCode" or
                "PreferredLanguageTag" or
                "Notes");
    }

    [Fact]
    public void Failed_outcome_does_not_change_the_profile()
    {
        GuestProfile profile = Create("Ada Guest", "ada@example.test", null);

        Result<GuestProfileUpdateOutcome> result = profile.UpdateWithOutcome(
            "Changed",
            null,
            "changed@example.test",
            null,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            profile.Notes,
            expectedVersion: profile.Version + 1,
            "user:operator-b",
            Guid.NewGuid(),
            Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal("Guests.VersionConflict", result.Error.Code);
        Assert.Equal("Ada Guest", profile.DisplayName);
        Assert.Equal("ada@example.test", profile.Email);
        Assert.Equal(1, profile.Version);
    }

    [Fact]
    public void Update_outcome_covers_every_correctable_profile_field()
    {
        GuestProfile profile = Create("Ada Guest", "ada@example.test", "+1 555 0100");

        Result<GuestProfileUpdateOutcome> result = profile.UpdateWithOutcome(
            "Ada Lovelace",
            "Augusta Ada King",
            "new@example.test",
            "+44 20 1234 5678",
            new DateOnly(1815, 12, 10),
            "GB",
            "en-GB",
            "Returning guest",
            profile.Version,
            "user:operator-b",
            Guid.NewGuid(),
            Now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                GuestProfileField.DisplayName,
                GuestProfileField.LegalName,
                GuestProfileField.Email,
                GuestProfileField.Phone,
                GuestProfileField.DateOfBirth,
                GuestProfileField.NationalityCountryCode,
                GuestProfileField.PreferredLanguageTag,
                GuestProfileField.Notes,
                GuestProfileField.LanguageTags
            ],
            result.Value.ChangedFields);
    }

    [Fact]
    public void Anonymise_clears_every_guest_and_search_value_and_preserves_staff_attribution()
    {
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "Ada Guest",
            "Augusta Ada King",
            "ada@example.test",
            "+44 20 1234 5678",
            new DateOnly(1815, 12, 10),
            "GB",
            "en-GB",
            "Returning guest",
            "user:creator",
            Guid.NewGuid(),
            Now.AddDays(-1)).Value;
        profile.ClearDomainEvents();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset completedAtUtc = Now.AddMinutes(1);

        Result<GuestProfileAnonymisationOutcome> result = profile.Anonymise(
            profile.Version,
            "user:privacy-executor",
            eventId,
            completedAtUtc);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PreviousVersion);
        Assert.Equal(2, result.Value.CurrentVersion);
        Assert.Equal(GuestProfileState.Anonymised, profile.Status);
        Assert.Equal(GuestProfile.AnonymisedDisplayName, profile.DisplayName);
        Assert.Equal("ANONYMISED GUEST", profile.DisplayNameSearch);
        Assert.Null(profile.LegalName);
        Assert.Null(profile.LegalNameSearch);
        Assert.Null(profile.Email);
        Assert.Null(profile.EmailSearch);
        Assert.Null(profile.Phone);
        Assert.Null(profile.PhoneSearch);
        Assert.Null(profile.DateOfBirth);
        Assert.Null(profile.NationalityCountryCode);
        Assert.Null(profile.PreferredLanguageTag);
        Assert.Null(profile.Notes);
        Assert.Null(profile.ArchivedAtUtc);
        Assert.Equal(completedAtUtc, profile.AnonymisedAtUtc);
        Assert.Equal("user:creator", profile.CreatedBy);
        Assert.Equal("user:privacy-executor", profile.LastChangedBy);
        Assert.True(profile.MatchesAnonymisedState(2, completedAtUtc));
        GuestProfileAnonymisedDomainEvent domainEvent =
            Assert.IsType<GuestProfileAnonymisedDomainEvent>(Assert.Single(profile.DomainEvents));
        Assert.Equal(eventId, domainEvent.EventId);
        Assert.Equal(profile.Id, domainEvent.GuestId);
        Assert.Equal(2, domainEvent.GuestVersion);
        Assert.DoesNotContain(
            typeof(GuestProfileAnonymisedDomainEvent).GetProperties(),
            property => property.Name is
                nameof(GuestProfile.DisplayName) or
                nameof(GuestProfile.LegalName) or
                nameof(GuestProfile.Email) or
                nameof(GuestProfile.Phone) or
                nameof(GuestProfile.Notes));
    }

    [Fact]
    public void Anonymise_rejects_stale_archived_and_repeated_transitions()
    {
        GuestProfile stale = Create("Guest", "guest@example.test", null);
        Assert.Equal(
            "Guests.VersionConflict",
            stale.Anonymise(
                stale.Version + 1,
                "user:privacy",
                Guid.NewGuid(),
                Now).Error.Code);
        Assert.Equal(GuestProfileState.Active, stale.Status);
        Assert.Equal("guest@example.test", stale.Email);

        GuestProfile archived = Create("Archived", null, null);
        Assert.True(archived.Archive(
            archived.Version,
            "user:operator",
            Guid.NewGuid(),
            Now).IsSuccess);
        Assert.Equal(
            "Guests.GuestNotActiveForAnonymisation",
            archived.Anonymise(
                archived.Version,
                "user:privacy",
                Guid.NewGuid(),
                Now.AddMinutes(1)).Error.Code);

        GuestProfile anonymised = Create("Anonymised", null, null);
        Assert.True(anonymised.Anonymise(
            anonymised.Version,
            "user:privacy",
            Guid.NewGuid(),
            Now).IsSuccess);
        Assert.Equal(
            "Guests.GuestAlreadyAnonymised",
            anonymised.Anonymise(
                anonymised.Version,
                "user:privacy",
                Guid.NewGuid(),
                Now.AddMinutes(1)).Error.Code);
    }

    [Fact]
    public void Restore_anonymisation_re_scrubs_archived_and_missing_profiles()
    {
        GuestProfile archived = Create(
            "Archived guest",
            "archived@example.test",
            "+1 555 0199");
        Assert.True(archived.Archive(
            archived.Version,
            "user:operator",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        archived.ClearDomainEvents();
        DateTimeOffset originallyCompletedAtUtc = Now.AddMinutes(2);
        DateTimeOffset replayedAtUtc = Now.AddHours(1);

        Assert.True(archived.RestoreAnonymisation(
            "data-rights-restore",
            Guid.NewGuid(),
            originallyCompletedAtUtc,
            replayedAtUtc).IsSuccess);
        Assert.True(archived.MatchesAnonymisedState(
            originallyCompletedAtUtc));
        Assert.Null(archived.ArchivedAtUtc);
        Assert.Equal(replayedAtUtc, archived.LastChangedAtUtc);

        Guid missingGuestId = Guid.NewGuid();
        Guid originPropertyId = Guid.NewGuid();
        GuestProfile restored = GuestProfile.RestoreMissingAnonymised(
            missingGuestId,
            "tenant-a",
            originPropertyId,
            "data-rights-restore",
            Guid.NewGuid(),
            originallyCompletedAtUtc,
            replayedAtUtc).Value;

        Assert.Equal(missingGuestId, restored.Id);
        Assert.Equal(originPropertyId, restored.OriginPropertyId);
        Assert.Equal(2, restored.Version);
        Assert.True(restored.MatchesAnonymisedState(
            originallyCompletedAtUtc));
        GuestProfileAnonymisedDomainEvent domainEvent =
            Assert.IsType<GuestProfileAnonymisedDomainEvent>(
                Assert.Single(restored.DomainEvents));
        Assert.Equal(replayedAtUtc, domainEvent.OccurredAtUtc);
    }

    [Fact]
    public void Retention_anonymises_archived_profile_with_distinct_proof()
    {
        GuestProfile profile = Create(
            "Archived guest",
            "archived@example.test",
            "+1 555 0199");
        Assert.True(profile.Archive(
            profile.Version,
            "user:operator",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        long selectedVersion = profile.Version;
        Guid executionId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset completedAtUtc = Now.AddYears(2);

        GuestProfileAnonymisationOutcome outcome =
            profile.AnonymiseForRetention(
                selectedVersion,
                "system:retention",
                eventId,
                completedAtUtc).Value;
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                profile.ScopeId,
                executionId,
                profile.Id,
                selectedVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                completedAtUtc.AddDays(-1),
                new string('a', 64),
                TimeZoneCatalog.Default.CatalogVersion,
                eventId,
                "system:retention",
                completedAtUtc).Value;
        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                profile.ScopeId,
                receipt).Value;

        Assert.True(profile.MatchesAnonymisedState(
            outcome.CurrentVersion,
            completedAtUtc));
        Assert.Equal(
            GuestAnonymisationAuthority.Retention,
            tombstone.Authority);
        Assert.True(tombstone.MatchesRetention(receipt));
        Assert.Equal(
            "Guests.AnonymisationTombstoneInvalid",
            tombstone.AttachRestoreProof(
                Guid.NewGuid(),
                completedAtUtc,
                receipt.CanonicalSha256,
                completedAtUtc).Error.Code);
    }

    [Fact]
    public void Restore_receipt_rejects_an_unreachable_tombstone_revision()
    {
        Result<GuestAnonymisationRestoreReceipt> result =
            GuestAnonymisationRestoreReceipt.Create(
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                ownerReceiptContractVersion: 1,
                Guid.NewGuid(),
                new string('a', GuestAnonymisationReceipt.Sha256Length),
                resultingGuestVersion: 2,
                tombstoneRevision:
                    GuestAnonymisationTombstone.MaximumRevision + 1,
                Now.AddMinutes(1));

        Assert.Equal(
            "Guests.AnonymisationRestoreReceiptInvalid",
            result.Error.Code);
    }

    private static GuestProfile Create(string displayName, string? email, string? phone) => GuestProfile.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        displayName,
        legalName: null,
        email,
        phone,
        new DateOnly(1990, 1, 1),
        "us",
        "en-US",
        notes: null,
        "  user:operator-a  ",
        Guid.NewGuid(),
        Now).Value;
}
