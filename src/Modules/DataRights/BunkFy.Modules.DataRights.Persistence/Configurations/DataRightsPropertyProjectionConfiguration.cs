namespace BunkFy.Modules.DataRights.Persistence.Configurations;

using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class DataRightsPropertyProjectionConfiguration
    : IEntityTypeConfiguration<DataRightsPropertyProjection>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<DataRightsPropertyProjection> builder)
    {
        builder.ToTable("property_projection", table =>
        {
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_versions",
                "\"TopologySourceVersion\" >= 0 AND \"PolicySourceVersion\" >= 0");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_topology",
                "(\"TopologySourceVersion\" = 0 AND \"Status\" = 0 AND " +
                "\"Name\" IS NULL AND \"TimeZoneId\" IS NULL) OR " +
                "(\"TopologySourceVersion\" >= 1 AND \"Status\" IN (1, 2) AND " +
                "(\"Status\" = 2 OR (\"Name\" IS NOT NULL AND " +
                "\"TimeZoneId\" IS NOT NULL)))");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_topology_text",
                "(\"Name\" IS NULL OR (char_length(\"Name\") > 0 AND " +
                "\"Name\" = btrim(\"Name\") AND \"Name\" !~ '[[:cntrl:]]')) AND " +
                "(\"TimeZoneId\" IS NULL OR (char_length(\"TimeZoneId\") > 0 AND " +
                "\"TimeZoneId\" = btrim(\"TimeZoneId\") AND " +
                "\"TimeZoneId\" !~ '[[:cntrl:]]'))");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_known",
                "\"IsKnown\" = (\"TopologySourceVersion\" >= 1 OR " +
                "\"PolicySourceVersion\" >= 1)");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_processing_status",
                "\"ProcessingStatus\" BETWEEN 1 AND 3");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_policy_source",
                "(\"PolicySourceVersion\" = 0 AND \"ProcessingStatus\" = 1) OR " +
                "\"PolicySourceVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_data_rights_property_projection_governance_policy",
                "(\"ProcessingStatus\" = 1 AND \"OperatingCountryCode\" IS NULL AND " +
                "\"JurisdictionPolicyId\" IS NULL AND \"JurisdictionPolicyVersion\" IS NULL AND " +
                "\"DataRegionId\" IS NULL AND \"TransferProfileId\" IS NULL AND " +
                "\"RetentionPolicyId\" IS NULL AND \"RetentionPolicyVersion\" IS NULL AND " +
                "\"PolicyContentSha256\" IS NULL AND \"PolicyEffectiveAtUtc\" IS NULL AND " +
                "\"PolicyExpiresAtUtc\" IS NULL AND \"PolicyActivatedAtUtc\" IS NULL) OR " +
                "(\"ProcessingStatus\" IN (2, 3) AND \"OperatingCountryCode\" IS NOT NULL AND " +
                "\"JurisdictionPolicyId\" IS NOT NULL AND \"JurisdictionPolicyVersion\" IS NOT NULL AND " +
                "\"DataRegionId\" IS NOT NULL AND \"TransferProfileId\" IS NOT NULL AND " +
                "\"RetentionPolicyId\" IS NOT NULL AND \"RetentionPolicyVersion\" IS NOT NULL AND " +
                "\"PolicyContentSha256\" IS NOT NULL AND \"PolicyEffectiveAtUtc\" IS NOT NULL AND " +
                "\"PolicyExpiresAtUtc\" IS NOT NULL AND \"PolicyActivatedAtUtc\" IS NOT NULL AND " +
                "\"JurisdictionPolicyVersion\" > 0 AND \"RetentionPolicyVersion\" > 0 AND " +
                "\"OperatingCountryCode\" ~ '^[A-Z]{2}$' AND " +
                "\"JurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]*$' AND " +
                "\"DataRegionId\" ~ '^[a-z][a-z0-9._-]*$' AND " +
                "\"TransferProfileId\" ~ '^[a-z][a-z0-9._-]*$' AND " +
                "\"RetentionPolicyId\" ~ '^[a-z][a-z0-9._-]*$' AND " +
                "\"PolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND " +
                "\"PolicyEffectiveAtUtc\" < \"PolicyExpiresAtUtc\" AND " +
                "\"PolicyActivatedAtUtc\" >= \"PolicyEffectiveAtUtc\" AND " +
                "\"PolicyActivatedAtUtc\" < \"PolicyExpiresAtUtc\")");
        });
        builder.HasKey(property => new { property.ScopeId, property.Id });
        builder.Property(property => property.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(property => property.Name)
            .HasMaxLength(PropertiesContractLimits.PropertyNameMaxLength);
        builder.Property(property => property.TimeZoneId)
            .HasMaxLength(PropertiesContractLimits.TimeZoneIdMaxLength);
        builder.Property(property => property.Status).HasConversion<int>().IsRequired();
        builder.Property(property => property.TopologySourceVersion)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(property => property.PolicySourceVersion)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(property => property.ProcessingStatus)
            .HasConversion<int>()
            .HasDefaultValue(PropertyProcessingStatus.Unconfigured)
            .HasSentinel(default)
            .IsRequired();
        builder.HasIndex(property => new { property.ScopeId, property.Status, property.Id });
        builder.OwnsOne(property => property.GovernancePolicy, policy =>
        {
            policy.Property(value => value.OperatingCountryCode)
                .HasColumnName("OperatingCountryCode")
                .HasMaxLength(PropertiesContractLimits.CountryCodeLength);
            policy.Property(value => value.PolicyId)
                .HasColumnName("JurisdictionPolicyId")
                .HasMaxLength(PropertiesContractLimits.PolicyKeyMaxLength);
            policy.Property(value => value.PolicyVersion)
                .HasColumnName("JurisdictionPolicyVersion");
            policy.Property(value => value.DataRegionId)
                .HasColumnName("DataRegionId")
                .HasMaxLength(PropertiesContractLimits.PolicyKeyMaxLength);
            policy.Property(value => value.TransferProfileId)
                .HasColumnName("TransferProfileId")
                .HasMaxLength(PropertiesContractLimits.PolicyKeyMaxLength);
            policy.Property(value => value.RetentionPolicyId)
                .HasColumnName("RetentionPolicyId")
                .HasMaxLength(PropertiesContractLimits.PolicyKeyMaxLength);
            policy.Property(value => value.RetentionPolicyVersion)
                .HasColumnName("RetentionPolicyVersion");
            policy.Property(value => value.ContentSha256)
                .HasColumnName("PolicyContentSha256")
                .HasMaxLength(PropertiesContractLimits.ContentSha256Length);
            policy.Property(value => value.PolicyEffectiveAtUtc)
                .HasColumnName("PolicyEffectiveAtUtc");
            policy.Property(value => value.PolicyExpiresAtUtc)
                .HasColumnName("PolicyExpiresAtUtc");
            policy.Property(value => value.ActivatedAtUtc)
                .HasColumnName("PolicyActivatedAtUtc");
            policy.OwnsMany(value => value.Acknowledgements, acknowledgements =>
            {
                acknowledgements.ToTable(
                    "property_policy_acknowledgements",
                    table => table.HasCheckConstraint(
                        "CK_data_rights_property_policy_acknowledgements_contract",
                        "\"AcknowledgementVersion\" >= 1 AND " +
                        "\"AcknowledgementId\" ~ '^[a-z][a-z0-9._-]*$'"));
                acknowledgements.WithOwner().HasForeignKey("ScopeId", "PropertyId");
                acknowledgements.Property<string>("ScopeId").HasMaxLength(128);
                acknowledgements.Property<Guid>("PropertyId");
                acknowledgements.HasKey(
                    "ScopeId",
                    "PropertyId",
                    nameof(DataRightsPropertyPolicyAcknowledgement.AcknowledgementId),
                    nameof(DataRightsPropertyPolicyAcknowledgement.AcknowledgementVersion));
                acknowledgements.Property(value => value.AcknowledgementId)
                    .HasMaxLength(PropertiesContractLimits.PolicyKeyMaxLength)
                    .IsRequired();
                acknowledgements.Property(value => value.AcknowledgementVersion).IsRequired();
            });
            policy.Navigation(value => value.Acknowledgements)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
