using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Properties.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyGovernanceRevisionTenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_property_governance_revision_coordinates",
                schema: "properties",
                table: "property_governance_revisions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_property_governance_revision_evidence",
                schema: "properties",
                table: "property_governance_revisions",
                sql: "(\"Action\" = 1 AND \"PreviousOperatingCountryCode\" IS NULL AND \"PreviousJurisdictionPolicyId\" IS NULL AND \"PreviousJurisdictionPolicyVersion\" IS NULL AND \"PreviousDataRegionId\" IS NULL AND \"PreviousTransferProfileId\" IS NULL AND \"PreviousRetentionPolicyId\" IS NULL AND \"PreviousRetentionPolicyVersion\" IS NULL AND \"PreviousPolicyContentSha256\" IS NULL AND \"PreviousAcknowledgementSetSha256\" IS NULL AND \"CurrentOperatingCountryCode\" IS NOT NULL AND \"CurrentJurisdictionPolicyId\" IS NOT NULL AND \"CurrentJurisdictionPolicyVersion\" IS NOT NULL AND \"CurrentDataRegionId\" IS NOT NULL AND \"CurrentTransferProfileId\" IS NOT NULL AND \"CurrentRetentionPolicyId\" IS NOT NULL AND \"CurrentRetentionPolicyVersion\" IS NOT NULL AND \"CurrentPolicyContentSha256\" IS NOT NULL AND \"CurrentAcknowledgementSetSha256\" IS NOT NULL) OR (\"Action\" = 2 AND \"PreviousOperatingCountryCode\" IS NOT NULL AND \"PreviousJurisdictionPolicyId\" IS NOT NULL AND \"PreviousJurisdictionPolicyVersion\" IS NOT NULL AND \"PreviousDataRegionId\" IS NOT NULL AND \"PreviousTransferProfileId\" IS NOT NULL AND \"PreviousRetentionPolicyId\" IS NOT NULL AND \"PreviousRetentionPolicyVersion\" IS NOT NULL AND \"PreviousPolicyContentSha256\" IS NOT NULL AND \"PreviousAcknowledgementSetSha256\" IS NOT NULL AND \"CurrentOperatingCountryCode\" IS NOT NULL AND \"CurrentJurisdictionPolicyId\" IS NOT NULL AND \"CurrentJurisdictionPolicyVersion\" IS NOT NULL AND \"CurrentDataRegionId\" IS NOT NULL AND \"CurrentTransferProfileId\" IS NOT NULL AND \"CurrentRetentionPolicyId\" IS NOT NULL AND \"CurrentRetentionPolicyVersion\" IS NOT NULL AND \"CurrentPolicyContentSha256\" IS NOT NULL AND \"CurrentAcknowledgementSetSha256\" IS NOT NULL AND NOT (\"PreviousOperatingCountryCode\" IS NOT DISTINCT FROM \"CurrentOperatingCountryCode\" AND \"PreviousJurisdictionPolicyId\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyId\" AND \"PreviousJurisdictionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyVersion\" AND \"PreviousDataRegionId\" IS NOT DISTINCT FROM \"CurrentDataRegionId\" AND \"PreviousTransferProfileId\" IS NOT DISTINCT FROM \"CurrentTransferProfileId\" AND \"PreviousRetentionPolicyId\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyId\" AND \"PreviousRetentionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyVersion\" AND \"PreviousPolicyContentSha256\" IS NOT DISTINCT FROM \"CurrentPolicyContentSha256\" AND \"PreviousAcknowledgementSetSha256\" IS NOT DISTINCT FROM \"CurrentAcknowledgementSetSha256\")) OR (\"Action\" IN (3, 4) AND \"PreviousOperatingCountryCode\" IS NOT NULL AND \"PreviousJurisdictionPolicyId\" IS NOT NULL AND \"PreviousJurisdictionPolicyVersion\" IS NOT NULL AND \"PreviousDataRegionId\" IS NOT NULL AND \"PreviousTransferProfileId\" IS NOT NULL AND \"PreviousRetentionPolicyId\" IS NOT NULL AND \"PreviousRetentionPolicyVersion\" IS NOT NULL AND \"PreviousPolicyContentSha256\" IS NOT NULL AND \"PreviousAcknowledgementSetSha256\" IS NOT NULL AND \"CurrentOperatingCountryCode\" IS NOT NULL AND \"CurrentJurisdictionPolicyId\" IS NOT NULL AND \"CurrentJurisdictionPolicyVersion\" IS NOT NULL AND \"CurrentDataRegionId\" IS NOT NULL AND \"CurrentTransferProfileId\" IS NOT NULL AND \"CurrentRetentionPolicyId\" IS NOT NULL AND \"CurrentRetentionPolicyVersion\" IS NOT NULL AND \"CurrentPolicyContentSha256\" IS NOT NULL AND \"CurrentAcknowledgementSetSha256\" IS NOT NULL AND \"PreviousOperatingCountryCode\" IS NOT DISTINCT FROM \"CurrentOperatingCountryCode\" AND \"PreviousJurisdictionPolicyId\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyId\" AND \"PreviousJurisdictionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentJurisdictionPolicyVersion\" AND \"PreviousDataRegionId\" IS NOT DISTINCT FROM \"CurrentDataRegionId\" AND \"PreviousTransferProfileId\" IS NOT DISTINCT FROM \"CurrentTransferProfileId\" AND \"PreviousRetentionPolicyId\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyId\" AND \"PreviousRetentionPolicyVersion\" IS NOT DISTINCT FROM \"CurrentRetentionPolicyVersion\" AND \"PreviousPolicyContentSha256\" IS NOT DISTINCT FROM \"CurrentPolicyContentSha256\" AND \"PreviousAcknowledgementSetSha256\" IS NOT DISTINCT FROM \"CurrentAcknowledgementSetSha256\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_property_governance_revision_occurred_at",
                schema: "properties",
                table: "property_governance_revisions",
                sql: "\"OccurredAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_property_governance_revision_policy",
                schema: "properties",
                table: "property_governance_revisions",
                sql: "\"CurrentOperatingCountryCode\" ~ '^[A-Z]{2}$' AND \"CurrentJurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"CurrentJurisdictionPolicyVersion\" > 0 AND \"CurrentDataRegionId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"CurrentTransferProfileId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"CurrentRetentionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"CurrentRetentionPolicyVersion\" > 0 AND \"CurrentPolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND \"CurrentAcknowledgementSetSha256\" ~ '^[0-9a-f]{64}$' AND (\"PreviousOperatingCountryCode\" IS NULL OR (\"PreviousOperatingCountryCode\" ~ '^[A-Z]{2}$' AND \"PreviousJurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"PreviousJurisdictionPolicyVersion\" > 0 AND \"PreviousDataRegionId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"PreviousTransferProfileId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"PreviousRetentionPolicyId\" ~ '^[a-z][a-z0-9._-]{0,127}$' AND \"PreviousRetentionPolicyVersion\" > 0 AND \"PreviousPolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND \"PreviousAcknowledgementSetSha256\" ~ '^[0-9a-f]{64}$'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_property_governance_revision_text",
                schema: "properties",
                table: "property_governance_revisions",
                sql: "char_length(\"DecisionReasonCode\") > 0 AND \"DecisionReasonCode\" = btrim(\"DecisionReasonCode\") AND \"DecisionReasonCode\" !~ '[[:cntrl:]]' AND char_length(\"ActorId\") > 0 AND \"ActorId\" = btrim(\"ActorId\") AND \"ActorId\" !~ '[[:cntrl:]]'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_property_governance_revision_coordinates",
                schema: "properties",
                table: "property_governance_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_property_governance_revision_evidence",
                schema: "properties",
                table: "property_governance_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_property_governance_revision_occurred_at",
                schema: "properties",
                table: "property_governance_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_property_governance_revision_policy",
                schema: "properties",
                table: "property_governance_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_property_governance_revision_text",
                schema: "properties",
                table: "property_governance_revisions");
        }
    }
}
