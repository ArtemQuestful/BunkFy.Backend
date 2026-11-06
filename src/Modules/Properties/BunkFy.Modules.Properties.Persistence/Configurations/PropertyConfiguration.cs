namespace BunkFy.Modules.Properties.Persistence.Configurations;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.ToTable("properties", table =>
        {
            table.HasCheckConstraint(
                "CK_properties_processing_state",
                "\"ProcessingState\" BETWEEN 1 AND 3");
            table.HasCheckConstraint(
                "CK_properties_governance_binding",
                "(\"ProcessingState\" = 1 AND \"OperatingCountryCode\" IS NULL AND " +
                "\"JurisdictionPolicyId\" IS NULL AND \"JurisdictionPolicyVersion\" IS NULL AND " +
                "\"DataRegionId\" IS NULL AND \"TransferProfileId\" IS NULL AND " +
                "\"RetentionPolicyId\" IS NULL AND \"RetentionPolicyVersion\" IS NULL AND " +
                "\"PolicyContentSha256\" IS NULL AND \"PolicyEffectiveAtUtc\" IS NULL AND " +
                "\"PolicyExpiresAtUtc\" IS NULL AND \"PolicyActivatedAtUtc\" IS NULL) OR " +
                "(\"ProcessingState\" IN (2, 3) AND \"OperatingCountryCode\" IS NOT NULL AND " +
                "\"JurisdictionPolicyId\" IS NOT NULL AND \"JurisdictionPolicyVersion\" IS NOT NULL AND " +
                "\"DataRegionId\" IS NOT NULL AND \"TransferProfileId\" IS NOT NULL AND " +
                "\"RetentionPolicyId\" IS NOT NULL AND \"RetentionPolicyVersion\" IS NOT NULL AND " +
                "\"PolicyContentSha256\" IS NOT NULL AND \"PolicyEffectiveAtUtc\" IS NOT NULL AND " +
                "\"PolicyExpiresAtUtc\" IS NOT NULL AND \"PolicyActivatedAtUtc\" IS NOT NULL AND " +
                "\"JurisdictionPolicyVersion\" > 0 AND \"RetentionPolicyVersion\" > 0 AND " +
                "char_length(\"OperatingCountryCode\") = 2 AND char_length(\"PolicyContentSha256\") = 64 AND " +
                "\"PolicyEffectiveAtUtc\" < \"PolicyExpiresAtUtc\" AND " +
                "\"PolicyActivatedAtUtc\" >= \"PolicyEffectiveAtUtc\" AND " +
                "\"PolicyActivatedAtUtc\" < \"PolicyExpiresAtUtc\")");
        });
        builder.HasKey(property => property.Id);
        builder.Property(property => property.Name)
            .HasConversion(name => name.Value, value => PropertyName.Create(value).Value)
            .HasMaxLength(Property.PropertyNameMaxLength)
            .IsRequired();
        builder.Property(property => property.Code)
            .HasConversion(code => code.Value, value => PropertyCode.Create(value).Value)
            .HasMaxLength(Property.PropertyCodeMaxLength)
            .IsRequired();
        builder.Property(property => property.TimeZoneId)
            .HasConversion(
                timeZone => timeZone.Value,
                value => PropertyTimeZoneId.RestorePersisted(value))
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(property => property.Status).HasConversion<int>().IsRequired();
        builder.Property(property => property.ProcessingState)
            .HasConversion<int>()
            .HasDefaultValue(PropertyProcessingState.Unconfigured)
            .HasSentinel(default)
            .IsRequired();
        builder.Property(property => property.Version)
            .HasDefaultValue(1L)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(property => property.ProjectionOrdinal)
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.HasAlternateKey(property => new { property.ScopeId, property.Id });
        builder.HasIndex(property => new { property.ScopeId, property.Code }).IsUnique();
        builder.HasIndex(property => property.ProjectionOrdinal).IsUnique();
        builder.HasIndex(property => new
        {
            property.ScopeId,
            property.ProjectionOrdinal,
            property.Id
        }).HasDatabaseName("IX_properties_scope_projection_ordinal_id");
        builder.OwnsOne(property => property.GovernanceBinding, binding =>
        {
            binding.Property(value => value.OperatingCountryCode)
                .HasColumnName("OperatingCountryCode")
                .HasMaxLength(Property.CountryCodeLength);
            binding.Property(value => value.PolicyId)
                .HasColumnName("JurisdictionPolicyId")
                .HasMaxLength(Property.PolicyKeyMaxLength);
            binding.Property(value => value.PolicyVersion)
                .HasColumnName("JurisdictionPolicyVersion");
            binding.Property(value => value.DataRegionId)
                .HasColumnName("DataRegionId")
                .HasMaxLength(Property.PolicyKeyMaxLength);
            binding.Property(value => value.TransferProfileId)
                .HasColumnName("TransferProfileId")
                .HasMaxLength(Property.PolicyKeyMaxLength);
            binding.Property(value => value.RetentionPolicyId)
                .HasColumnName("RetentionPolicyId")
                .HasMaxLength(Property.PolicyKeyMaxLength);
            binding.Property(value => value.RetentionPolicyVersion)
                .HasColumnName("RetentionPolicyVersion");
            binding.Property(value => value.ContentSha256)
                .HasColumnName("PolicyContentSha256")
                .HasMaxLength(Property.ContentSha256Length);
            binding.Property(value => value.PolicyEffectiveAtUtc)
                .HasColumnName("PolicyEffectiveAtUtc");
            binding.Property(value => value.PolicyExpiresAtUtc)
                .HasColumnName("PolicyExpiresAtUtc");
            binding.Property(value => value.ActivatedAtUtc)
                .HasColumnName("PolicyActivatedAtUtc");
        });
        builder.OwnsMany(property => property.GovernanceAcknowledgements, acknowledgements =>
        {
            acknowledgements.ToTable("property_governance_acknowledgements");
            acknowledgements.WithOwner().HasForeignKey("PropertyId");
            acknowledgements.Property<Guid>("PropertyId");
            acknowledgements.HasKey(
                "PropertyId",
                nameof(PropertyGovernanceAcknowledgement.AcknowledgementId),
                nameof(PropertyGovernanceAcknowledgement.AcknowledgementVersion));
            acknowledgements.Property(acknowledgement => acknowledgement.AcknowledgementId)
                .HasMaxLength(Property.PolicyKeyMaxLength)
                .IsRequired();
            acknowledgements.Property(acknowledgement => acknowledgement.AcknowledgementVersion)
                .IsRequired();
        });
        builder.Navigation(property => property.GovernanceAcknowledgements)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
