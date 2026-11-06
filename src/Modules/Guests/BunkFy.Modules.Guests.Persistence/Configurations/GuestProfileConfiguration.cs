namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestProfileConfiguration : IEntityTypeConfiguration<GuestProfile>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestProfile> builder)
    {
        builder.ToTable("guest_profiles", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_profiles_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"OriginPropertyId\" <> '{EmptyGuid}' AND " +
                $"(\"CreationConfirmationId\" IS NULL OR \"CreationConfirmationId\" <> '{EmptyGuid}') AND " +
                "trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint("CK_guest_profiles_version", "\"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_profiles_language_tags",
                "cardinality(\"LanguageTags\") BETWEEN 0 AND 12 AND " +
                "array_position(\"LanguageTags\", NULL) IS NULL");
            table.HasCheckConstraint(
                "CK_guest_profiles_projection_ordinal",
                "\"ProjectionOrdinal\" >= 1");
            table.HasCheckConstraint("CK_guest_profiles_display_name", "length(trim(\"DisplayName\")) > 0");
            table.HasCheckConstraint("CK_guest_profiles_created_by", "length(trim(\"CreatedBy\")) > 0");
            table.HasCheckConstraint("CK_guest_profiles_last_changed_by", "length(trim(\"LastChangedBy\")) > 0");
            table.HasCheckConstraint(
                "CK_guest_profiles_search_shape",
                "trim(\"DisplayNameSearch\") <> '' AND " +
                PairedOptionalText("LegalName", "LegalNameSearch") + " AND " +
                PairedOptionalText("Email", "EmailSearch") + " AND " +
                PairedOptionalText("Phone", "PhoneSearch"));
            table.HasCheckConstraint(
                "CK_guest_profiles_optional_text",
                OptionalText("NationalityCountryCode") + " AND " +
                OptionalText("PreferredLanguageTag") + " AND " +
                OptionalText("Notes"));
            table.HasCheckConstraint(
                "CK_guest_profiles_state_versions",
                "(\"Status\" = 1 AND \"Version\" >= 1) OR " +
                "(\"Status\" IN (2, 3) AND \"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_guest_profiles_lifecycle",
                "(\"Status\" = 1 AND \"ArchivedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 2 AND \"ArchivedAtUtc\" IS NOT NULL AND " +
                "\"ArchivedAtUtc\" >= \"CreatedAtUtc\" AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 3 AND \"ArchivedAtUtc\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"CreatedAtUtc\")");
            table.HasCheckConstraint(
                "CK_guest_profiles_timestamps",
                "\"LastChangedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "(\"ArchivedAtUtc\" IS NULL OR \"ArchivedAtUtc\" = \"LastChangedAtUtc\") AND " +
                "(\"AnonymisedAtUtc\" IS NULL OR (\"AnonymisedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"AnonymisedAtUtc\" <= \"LastChangedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_guest_profiles_anonymised_profile",
                $"\"Status\" <> 3 OR (\"DisplayName\" = '{GuestProfile.AnonymisedDisplayName}' AND " +
                $"\"DisplayNameSearch\" = '{GuestProfile.AnonymisedDisplayName.ToUpperInvariant()}' AND " +
                "\"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND " +
                "\"Email\" IS NULL AND \"EmailSearch\" IS NULL AND " +
                "\"Phone\" IS NULL AND \"PhoneSearch\" IS NULL AND " +
                "\"DateOfBirth\" IS NULL AND \"NationalityCountryCode\" IS NULL AND " +
                "\"PreferredLanguageTag\" IS NULL AND cardinality(\"LanguageTags\") = 0 AND \"Notes\" IS NULL AND " +
                "\"CreationConfirmationId\" IS NULL AND \"ArchivedAtUtc\" IS NULL)");
        });
        builder.HasKey(profile => profile.Id);
        builder.HasAlternateKey(profile => new { profile.ScopeId, profile.Id });
        builder.Property(profile => profile.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(profile => profile.CreationConfirmationId);
        builder.HasIndex(profile => new
        {
            profile.ScopeId,
            profile.CreationConfirmationId
        }).IsUnique();
        builder.Property(profile => profile.DisplayName).HasMaxLength(GuestProfile.DisplayNameMaxLength).IsRequired();
        builder.Property(profile => profile.DisplayNameSearch).HasMaxLength(GuestProfile.DisplayNameMaxLength).IsRequired();
        builder.Property(profile => profile.LegalName).HasMaxLength(GuestProfile.LegalNameMaxLength);
        builder.Property(profile => profile.LegalNameSearch).HasMaxLength(GuestProfile.LegalNameMaxLength);
        builder.Property(profile => profile.Email).HasMaxLength(GuestProfile.EmailMaxLength);
        builder.Property(profile => profile.EmailSearch).HasMaxLength(GuestProfile.EmailMaxLength);
        builder.Property(profile => profile.Phone).HasMaxLength(GuestProfile.PhoneMaxLength);
        builder.Property(profile => profile.PhoneSearch).HasMaxLength(GuestProfile.PhoneMaxLength);
        builder.Property(profile => profile.NationalityCountryCode)
            .HasMaxLength(GuestProfile.CountryCodeLength)
            .IsFixedLength();
        builder.Property(profile => profile.PreferredLanguageTag).HasMaxLength(GuestProfile.LanguageTagMaxLength);
        builder.Property(profile => profile.LanguageTags)
            .HasField("languageTags")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasColumnType("text[]")
            .HasDefaultValueSql("ARRAY[]::text[]")
            .IsRequired();
        builder.Property(profile => profile.Notes).HasMaxLength(GuestProfile.NotesMaxLength);
        builder.Property(profile => profile.Status).HasConversion<int>().IsRequired();
        builder.Property(profile => profile.Version).IsConcurrencyToken().IsRequired();
        builder.Property(profile => profile.ProjectionOrdinal).ValueGeneratedOnAdd().IsRequired();
        builder.HasIndex(profile => profile.ProjectionOrdinal).IsUnique();
        builder.HasIndex(profile => new
        {
            profile.ScopeId,
            profile.Status,
            profile.ProjectionOrdinal
        });
        builder.Property(profile => profile.CreatedBy).HasMaxLength(GuestProfile.ActorIdMaxLength).IsRequired();
        builder.Property(profile => profile.LastChangedBy).HasMaxLength(GuestProfile.ActorIdMaxLength).IsRequired();
        builder.HasIndex(profile => new
        {
            profile.ScopeId,
            profile.OriginPropertyId,
            profile.Status,
            profile.DisplayNameSearch,
            profile.Id
        });
        builder.HasIndex(profile => new { profile.ScopeId, profile.EmailSearch });
        builder.HasIndex(profile => new { profile.ScopeId, profile.PhoneSearch });
        builder.Ignore(profile => profile.DomainEvents);
    }

    private static string PairedOptionalText(string value, string search) =>
        $"((\"{value}\" IS NULL AND \"{search}\" IS NULL) OR " +
        $"(\"{value}\" IS NOT NULL AND \"{search}\" IS NOT NULL AND " +
        $"trim(\"{value}\") <> '' AND trim(\"{search}\") <> ''))";

    private static string OptionalText(string value) =>
        $"(\"{value}\" IS NULL OR trim(\"{value}\") <> '')";
}
