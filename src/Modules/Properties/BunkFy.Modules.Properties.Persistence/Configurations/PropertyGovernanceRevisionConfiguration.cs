namespace BunkFy.Modules.Properties.Persistence.Configurations;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class PropertyGovernanceRevisionConfiguration
    : IEntityTypeConfiguration<PropertyGovernanceRevision>
{
    private const string CurrentComplete =
        "\"CurrentOperatingCountryCode\" IS NOT NULL AND " +
        "\"CurrentJurisdictionPolicyId\" IS NOT NULL AND " +
        "\"CurrentJurisdictionPolicyVersion\" IS NOT NULL AND " +
        "\"CurrentDataRegionId\" IS NOT NULL AND " +
        "\"CurrentTransferProfileId\" IS NOT NULL AND " +
        "\"CurrentRetentionPolicyId\" IS NOT NULL AND " +
        "\"CurrentRetentionPolicyVersion\" IS NOT NULL AND " +
        "\"CurrentPolicyContentSha256\" IS NOT NULL AND " +
        "\"CurrentAcknowledgementSetSha256\" IS NOT NULL";
    private const string PreviousComplete =
        "\"PreviousOperatingCountryCode\" IS NOT NULL AND " +
        "\"PreviousJurisdictionPolicyId\" IS NOT NULL AND " +
        "\"PreviousJurisdictionPolicyVersion\" IS NOT NULL AND " +
        "\"PreviousDataRegionId\" IS NOT NULL AND " +
        "\"PreviousTransferProfileId\" IS NOT NULL AND " +
        "\"PreviousRetentionPolicyId\" IS NOT NULL AND " +
        "\"PreviousRetentionPolicyVersion\" IS NOT NULL AND " +
        "\"PreviousPolicyContentSha256\" IS NOT NULL AND " +
        "\"PreviousAcknowledgementSetSha256\" IS NOT NULL";
    private const string PreviousAbsent =
        "\"PreviousOperatingCountryCode\" IS NULL AND " +
        "\"PreviousJurisdictionPolicyId\" IS NULL AND " +
        "\"PreviousJurisdictionPolicyVersion\" IS NULL AND " +
        "\"PreviousDataRegionId\" IS NULL AND " +
        "\"PreviousTransferProfileId\" IS NULL AND " +
        "\"PreviousRetentionPolicyId\" IS NULL AND " +
        "\"PreviousRetentionPolicyVersion\" IS NULL AND " +
        "\"PreviousPolicyContentSha256\" IS NULL AND " +
        "\"PreviousAcknowledgementSetSha256\" IS NULL";
    private const string PreviousMatchesCurrent =
        "\"PreviousOperatingCountryCode\" IS NOT DISTINCT FROM \"CurrentOperatingCountryCode\" AND " +
        "\"PreviousJurisdictionPolicyId\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyId\" AND " +
        "\"PreviousJurisdictionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyVersion\" AND " +
        "\"PreviousDataRegionId\" IS NOT DISTINCT FROM \"CurrentDataRegionId\" AND " +
        "\"PreviousTransferProfileId\" IS NOT DISTINCT FROM \"CurrentTransferProfileId\" AND " +
        "\"PreviousRetentionPolicyId\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyId\" AND " +
        "\"PreviousRetentionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyVersion\" AND " +
        "\"PreviousPolicyContentSha256\" IS NOT DISTINCT FROM \"CurrentPolicyContentSha256\" AND " +
        "\"PreviousAcknowledgementSetSha256\" IS NOT DISTINCT FROM \"CurrentAcknowledgementSetSha256\"";

    public void Configure(EntityTypeBuilder<PropertyGovernanceRevision> builder)
    {
        builder.ToTable("property_governance_revisions", table =>
        {
            table.HasCheckConstraint("CK_property_governance_revision_version", "\"PropertyVersion\" >= 2");
            table.HasCheckConstraint("CK_property_governance_revision_action", "\"Action\" BETWEEN 1 AND 4");
            table.HasCheckConstraint(
                "CK_property_governance_revision_coordinates",
                "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND " +
                "\"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND " +
                "char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_property_governance_revision_text",
                "char_length(\"DecisionReasonCode\") > 0 AND " +
                "\"DecisionReasonCode\" = btrim(\"DecisionReasonCode\") AND " +
                "\"DecisionReasonCode\" !~ '[[:cntrl:]]' AND " +
                "char_length(\"ActorId\") > 0 AND " +
                "\"ActorId\" = btrim(\"ActorId\") AND " +
                "\"ActorId\" !~ '[[:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_property_governance_revision_evidence",
                $"(\"Action\" = 1 AND {PreviousAbsent} AND {CurrentComplete}) OR " +
                $"(\"Action\" = 2 AND {PreviousComplete} AND {CurrentComplete} AND NOT ({PreviousMatchesCurrent})) OR " +
                $"(\"Action\" IN (3, 4) AND {PreviousComplete} AND {CurrentComplete} AND {PreviousMatchesCurrent})");
            table.HasCheckConstraint(
                "CK_property_governance_revision_policy",
                "\"CurrentOperatingCountryCode\" ~ '^[A-Z]{2}$' AND " +
                "\"CurrentJurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"CurrentJurisdictionPolicyVersion\" > 0 AND " +
                "\"CurrentDataRegionId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"CurrentTransferProfileId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"CurrentRetentionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"CurrentRetentionPolicyVersion\" > 0 AND " +
                "\"CurrentPolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND " +
                "\"CurrentAcknowledgementSetSha256\" ~ '^[0-9a-f]{64}$' AND " +
                "(\"PreviousOperatingCountryCode\" IS NULL OR (" +
                "\"PreviousOperatingCountryCode\" ~ '^[A-Z]{2}$' AND " +
                "\"PreviousJurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"PreviousJurisdictionPolicyVersion\" > 0 AND " +
                "\"PreviousDataRegionId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"PreviousTransferProfileId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"PreviousRetentionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND " +
                "\"PreviousRetentionPolicyVersion\" > 0 AND " +
                "\"PreviousPolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND " +
                "\"PreviousAcknowledgementSetSha256\" ~ '^[0-9a-f]{64}$'))");
            table.HasCheckConstraint(
                "CK_property_governance_revision_occurred_at",
                "\"OccurredAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
        });
        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(revision => revision.Action).HasConversion<int>().IsRequired();
        builder.Property(revision => revision.DecisionReasonCode)
            .HasMaxLength(PropertyGovernanceRevision.DecisionReasonCodeMaxLength)
            .IsRequired();
        builder.Property(revision => revision.ActorId).HasMaxLength(Property.ActorIdMaxLength).IsRequired();
        builder.HasIndex(revision => new
        {
            revision.ScopeId,
            revision.PropertyId,
            revision.PropertyVersion
        }).IsUnique();
        builder.HasIndex(revision => new
        {
            revision.ScopeId,
            revision.PropertyId,
            revision.OccurredAtUtc
        });
        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(revision => new { revision.ScopeId, revision.PropertyId })
            .HasPrincipalKey(property => new { property.ScopeId, property.Id })
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureCoordinates(builder.OwnsOne(revision => revision.Previous), "Previous");
        ConfigureCoordinates(builder.OwnsOne(revision => revision.Current), "Current");
    }

    private static void ConfigureCoordinates(
        OwnedNavigationBuilder<PropertyGovernanceRevision, PropertyGovernanceRevisionCoordinatesRecord> coordinates,
        string prefix)
    {
        coordinates.Property(value => value.OperatingCountryCode)
            .HasColumnName($"{prefix}OperatingCountryCode")
            .HasMaxLength(Property.CountryCodeLength);
        coordinates.Property(value => value.PolicyId)
            .HasColumnName($"{prefix}JurisdictionPolicyId")
            .HasMaxLength(Property.PolicyKeyMaxLength);
        coordinates.Property(value => value.PolicyVersion)
            .HasColumnName($"{prefix}JurisdictionPolicyVersion");
        coordinates.Property(value => value.DataRegionId)
            .HasColumnName($"{prefix}DataRegionId")
            .HasMaxLength(Property.PolicyKeyMaxLength);
        coordinates.Property(value => value.TransferProfileId)
            .HasColumnName($"{prefix}TransferProfileId")
            .HasMaxLength(Property.PolicyKeyMaxLength);
        coordinates.Property(value => value.RetentionPolicyId)
            .HasColumnName($"{prefix}RetentionPolicyId")
            .HasMaxLength(Property.PolicyKeyMaxLength);
        coordinates.Property(value => value.RetentionPolicyVersion)
            .HasColumnName($"{prefix}RetentionPolicyVersion");
        coordinates.Property(value => value.ContentSha256)
            .HasColumnName($"{prefix}PolicyContentSha256")
            .HasMaxLength(Property.ContentSha256Length);
        coordinates.Property(value => value.AcknowledgementSetSha256)
            .HasColumnName($"{prefix}AcknowledgementSetSha256")
            .HasMaxLength(Property.ContentSha256Length);
    }
}
