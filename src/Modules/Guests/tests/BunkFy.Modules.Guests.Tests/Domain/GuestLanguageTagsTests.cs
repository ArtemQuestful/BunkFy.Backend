namespace BunkFy.Modules.Guests.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestLanguageTagsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Legacy_create_preserves_scalar_and_derives_collection_without_registry_validation()
    {
        GuestProfile profile = Create("UnKnOwN-١");
        Assert.Equal("UnKnOwN-١", profile.PreferredLanguageTag);
        Assert.Equal("UnKnOwN-١", Assert.Single(profile.LanguageTags));
        Assert.Empty(Create(null).LanguageTags);
    }

    [Fact]
    public void Explicit_create_normalizes_identity_without_inventing_primary_and_protects_copies()
    {
        string[] input = [" FR ", "en-GB", "fr", "unknown-١"];
        GuestProfile profile = Create(null, input);
        input[0] = "de";
        string[] returned = profile.LanguageTags;
        returned[0] = "de";
        Assert.Null(profile.PreferredLanguageTag);
        Assert.Collection(profile.LanguageTags,
            value => Assert.Equal("en-gb", value),
            value => Assert.Equal("fr", value),
            value => Assert.Equal("unknown-١", value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("en_US")]
    [InlineData("fr FR")]
    [InlineData("é")]
    [InlineData("\uFEFFen")]
    [InlineData("en-𝟙")]
    public void Invalid_member_rejects_the_entire_change(string? tag)
    {
        GuestProfile profile = Create("EN");
        Assert.True(Update(profile, "EN", ["en", tag!]).IsFailure);
        Assert.Equal(1, profile.Version);
        Assert.Equal("EN", Assert.Single(profile.LanguageTags));
    }

    [Fact]
    public void Legacy_dotnet_trim_and_utf16_digit_rules_remain_the_admission_boundary()
    {
        GuestProfile profile = Create(null, ["\u0085En-١\u0085"]);
        Assert.Equal("en-١", Assert.Single(profile.LanguageTags));
    }

    [Fact]
    public void Count_and_length_limits_include_raw_duplicates_and_do_not_silently_drop_entries()
    {
        GuestProfile profile = Create(null);
        Assert.True(Update(profile, null, Enumerable.Repeat("en", 13).ToArray()).IsFailure);
        Assert.True(Update(profile, null, [new string('a', 36)]).IsFailure);
        Assert.True(Update(profile, null, Enumerable.Range(0, 12).Select(index => $"x-{index}").ToArray()).IsSuccess);
        long version = profile.Version;
        Assert.True(Update(profile, "de").IsFailure);
        Assert.Equal(version, profile.Version);
        Assert.Null(profile.PreferredLanguageTag);
        Assert.Equal(12, profile.LanguageTags.Length);
    }

    [Fact]
    public void Legacy_updates_preserve_secondaries_and_scalar_null_does_not_clear_collection()
    {
        GuestProfile profile = Create("EN");
        Assert.True(Update(profile, "EN", ["fr", "en"]).IsSuccess);
        Assert.True(Update(profile, "EN").IsSuccess);
        Assert.Contains("EN", profile.LanguageTags);
        Assert.Contains("fr", profile.LanguageTags);
        Assert.True(Update(profile, "DE").IsSuccess);
        Assert.Equal("DE", profile.PreferredLanguageTag);
        Assert.Contains("de", profile.LanguageTags);
        Assert.True(Update(profile, null).IsSuccess);
        Assert.Null(profile.PreferredLanguageTag);
        Assert.Equal(3, profile.LanguageTags.Length);
    }

    [Fact]
    public void Explicit_replace_preserves_existing_representatives_and_clear_is_distinct_from_omission()
    {
        GuestProfile profile = Create("EN-gB");
        Assert.True(Update(profile, "EN-gB", ["fr", "en-GB"]).IsSuccess);
        Assert.Contains("EN-gB", profile.LanguageTags);
        Assert.True(Update(profile, "EN-gB", []).IsFailure);
        Assert.True(Update(profile, null, []).IsSuccess);
        Assert.Empty(profile.LanguageTags);
    }

    [Fact]
    public void Equivalent_collection_has_no_changed_field_but_ordinary_version_behavior_is_retained()
    {
        GuestProfile profile = Create(null, ["en", "fr"]);
        Result<GuestProfileUpdateOutcome> result = Update(profile, null, ["FR", "EN", "en"]);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.ChangedFields);
        Assert.Equal(2, profile.Version);
        Assert.True(profile.HasValues(Change(null, ["fr", "en"])));
        Assert.False(profile.HasValues(Change(null, ["fr"])));
        Assert.True(profile.HasValues(Change(null)));
        Result<GuestProfileUpdateOutcome> replacement = Update(profile, null, ["de"]);
        Assert.Equal(GuestProfileField.LanguageTags, Assert.Single(replacement.Value.ChangedFields));
    }

    [Fact]
    public void Creation_replay_compares_resolved_membership_even_when_request_collection_was_omitted()
    {
        GuestProfile profile = Create("EN");
        GuestProfileCreationSnapshot legacy = Snapshot(profile, "EN", null);
        Assert.True(profile.MatchesCreation(legacy));
        Assert.True(profile.MatchesCreation(Snapshot(profile, "EN", ["en"])));
        Assert.True(Update(profile, "EN", ["en", "fr"]).IsSuccess);
        Assert.False(profile.MatchesCreation(legacy));
        Assert.False(profile.MatchesCreation(Snapshot(profile, "EN", ["en"])));
        Assert.True(profile.MatchesCreation(Snapshot(profile, "EN", ["FR", "en"])));
        GuestProfile empty = Create(null);
        Assert.True(empty.MatchesCreation(Snapshot(empty, null, [])));
    }

    private static GuestProfile Create(string? preferred, string[]? tags = null) => GuestProfile.Create(
        Guid.NewGuid(), "tenant-a", Guid.NewGuid(), "Language guest", null, null, null,
        null, null, preferred, null, "operator", Guid.NewGuid(), Now, languageTags: tags).Value;

    private static Result<GuestProfileUpdateOutcome> Update(
        GuestProfile profile, string? preferred, string[]? tags = null) => profile.UpdateWithOutcome(
        profile.DisplayName, null, null, null, null, null, preferred, null,
        profile.Version, "operator", Guid.NewGuid(), Now, tags);

    private static GuestProfileChange Change(string? preferred, string[]? tags = null) =>
        GuestProfileChange.Create("Language guest", null, null, null, null, null,
            preferred, null, "operator", Now, tags).Value;

    private static GuestProfileCreationSnapshot Snapshot(
        GuestProfile profile, string? preferred, string[]? tags) => GuestProfileCreationSnapshot.Capture(
        profile.OriginPropertyId, profile.DisplayName, null, null, null, null, null,
        preferred, null, "operator", Now, languageTags: tags).Value;
}
