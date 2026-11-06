using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddDataRightsPropertyProjectionTenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_governance_policy",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_coordinates",
                schema: "data-rights",
                table: "property_projection",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_governance_policy",
                schema: "data-rights",
                table: "property_projection",
                sql: "(\"ProcessingStatus\" = 1 AND \"OperatingCountryCode\" IS NULL AND \"JurisdictionPolicyId\" IS NULL AND \"JurisdictionPolicyVersion\" IS NULL AND \"DataRegionId\" IS NULL AND \"TransferProfileId\" IS NULL AND \"RetentionPolicyId\" IS NULL AND \"RetentionPolicyVersion\" IS NULL AND \"PolicyContentSha256\" IS NULL AND \"PolicyEffectiveAtUtc\" IS NULL AND \"PolicyExpiresAtUtc\" IS NULL AND \"PolicyActivatedAtUtc\" IS NULL) OR (\"ProcessingStatus\" IN (2, 3) AND \"OperatingCountryCode\" IS NOT NULL AND \"JurisdictionPolicyId\" IS NOT NULL AND \"JurisdictionPolicyVersion\" IS NOT NULL AND \"DataRegionId\" IS NOT NULL AND \"TransferProfileId\" IS NOT NULL AND \"RetentionPolicyId\" IS NOT NULL AND \"RetentionPolicyVersion\" IS NOT NULL AND \"PolicyContentSha256\" IS NOT NULL AND \"PolicyEffectiveAtUtc\" IS NOT NULL AND \"PolicyExpiresAtUtc\" IS NOT NULL AND \"PolicyActivatedAtUtc\" IS NOT NULL AND \"JurisdictionPolicyVersion\" > 0 AND \"RetentionPolicyVersion\" > 0 AND \"OperatingCountryCode\" ~ '^[A-Z]{2}$' AND \"JurisdictionPolicyId\" ~ '^[a-z][a-z0-9._-]*$' AND \"DataRegionId\" ~ '^[a-z][a-z0-9._-]*$' AND \"TransferProfileId\" ~ '^[a-z][a-z0-9._-]*$' AND \"RetentionPolicyId\" ~ '^[a-z][a-z0-9._-]*$' AND \"PolicyContentSha256\" ~ '^[0-9a-f]{64}$' AND \"PolicyEffectiveAtUtc\" < \"PolicyExpiresAtUtc\" AND \"PolicyActivatedAtUtc\" >= \"PolicyEffectiveAtUtc\" AND \"PolicyActivatedAtUtc\" < \"PolicyExpiresAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_known",
                schema: "data-rights",
                table: "property_projection",
                sql: "\"IsKnown\" = (\"TopologySourceVersion\" >= 1 OR \"PolicySourceVersion\" >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_policy_source",
                schema: "data-rights",
                table: "property_projection",
                sql: "(\"PolicySourceVersion\" = 0 AND \"ProcessingStatus\" = 1) OR \"PolicySourceVersion\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_topology",
                schema: "data-rights",
                table: "property_projection",
                sql: "(\"TopologySourceVersion\" = 0 AND \"Status\" = 0 AND \"Name\" IS NULL AND \"TimeZoneId\" IS NULL) OR (\"TopologySourceVersion\" >= 1 AND \"Status\" IN (1, 2) AND (\"Status\" = 2 OR (\"Name\" IS NOT NULL AND \"TimeZoneId\" IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_topology_text",
                schema: "data-rights",
                table: "property_projection",
                sql: "(\"Name\" IS NULL OR (char_length(\"Name\") > 0 AND \"Name\" = btrim(\"Name\") AND \"Name\" !~ '[[:cntrl:]]')) AND (\"TimeZoneId\" IS NULL OR (char_length(\"TimeZoneId\") > 0 AND \"TimeZoneId\" = btrim(\"TimeZoneId\") AND \"TimeZoneId\" !~ '[[:cntrl:]]'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_policy_acknowledgements_contract",
                schema: "data-rights",
                table: "property_policy_acknowledgements",
                sql: "\"AcknowledgementVersion\" >= 1 AND \"AcknowledgementId\" ~ '^[a-z][a-z0-9._-]*$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_coordinates",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_governance_policy",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_known",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_policy_source",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_topology",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_projection_topology_text",
                schema: "data-rights",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_property_policy_acknowledgements_contract",
                schema: "data-rights",
                table: "property_policy_acknowledgements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_property_projection_governance_policy",
                schema: "data-rights",
                table: "property_projection",
                sql: "(\"ProcessingStatus\" = 1 AND \"OperatingCountryCode\" IS NULL AND \"JurisdictionPolicyId\" IS NULL AND \"JurisdictionPolicyVersion\" IS NULL AND \"DataRegionId\" IS NULL AND \"TransferProfileId\" IS NULL AND \"RetentionPolicyId\" IS NULL AND \"RetentionPolicyVersion\" IS NULL AND \"PolicyContentSha256\" IS NULL AND \"PolicyEffectiveAtUtc\" IS NULL AND \"PolicyExpiresAtUtc\" IS NULL AND \"PolicyActivatedAtUtc\" IS NULL) OR (\"ProcessingStatus\" IN (2, 3) AND \"OperatingCountryCode\" IS NOT NULL AND \"JurisdictionPolicyId\" IS NOT NULL AND \"JurisdictionPolicyVersion\" IS NOT NULL AND \"DataRegionId\" IS NOT NULL AND \"TransferProfileId\" IS NOT NULL AND \"RetentionPolicyId\" IS NOT NULL AND \"RetentionPolicyVersion\" IS NOT NULL AND \"PolicyContentSha256\" IS NOT NULL AND \"PolicyEffectiveAtUtc\" IS NOT NULL AND \"PolicyExpiresAtUtc\" IS NOT NULL AND \"PolicyActivatedAtUtc\" IS NOT NULL AND \"JurisdictionPolicyVersion\" > 0 AND \"RetentionPolicyVersion\" > 0 AND char_length(\"OperatingCountryCode\") = 2 AND char_length(\"PolicyContentSha256\") = 64 AND \"PolicyEffectiveAtUtc\" < \"PolicyExpiresAtUtc\" AND \"PolicyActivatedAtUtc\" >= \"PolicyEffectiveAtUtc\" AND \"PolicyActivatedAtUtc\" < \"PolicyExpiresAtUtc\")");
        }
    }
}
