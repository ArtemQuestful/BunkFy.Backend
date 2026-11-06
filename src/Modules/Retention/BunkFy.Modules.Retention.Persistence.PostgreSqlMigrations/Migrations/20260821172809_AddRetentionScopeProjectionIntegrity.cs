using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Retention.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionScopeProjectionIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_property_projection_policy",
                schema: "retention",
                table: "property_projection");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_tenant_projection_coordinates",
                schema: "retention",
                table: "tenant_projection",
                sql: "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_property_projection_coordinates",
                schema: "retention",
                table: "property_projection",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_property_projection_known",
                schema: "retention",
                table: "property_projection",
                sql: "\"IsKnown\" = (\"TopologySourceVersion\" >= 1 OR \"PolicySourceVersion\" >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_property_projection_policy",
                schema: "retention",
                table: "property_projection",
                sql: "(\"PolicySourceVersion\" = 0 AND \"RetentionPolicyVersion\" IS NULL AND \"IsProcessingEnabled\" = FALSE) OR (\"PolicySourceVersion\" >= 1 AND \"RetentionPolicyVersion\" IS NOT NULL AND \"RetentionPolicyVersion\" >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_property_projection_topology",
                schema: "retention",
                table: "property_projection",
                sql: "(\"TopologySourceVersion\" = 0 AND \"IsActive\" = FALSE) OR \"TopologySourceVersion\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_tenant_projection_coordinates",
                schema: "retention",
                table: "tenant_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_property_projection_coordinates",
                schema: "retention",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_property_projection_known",
                schema: "retention",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_property_projection_policy",
                schema: "retention",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_property_projection_topology",
                schema: "retention",
                table: "property_projection");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_property_projection_policy",
                schema: "retention",
                table: "property_projection",
                sql: "(\"PolicySourceVersion\" = 0 AND \"RetentionPolicyVersion\" IS NULL AND \"IsProcessingEnabled\" = FALSE) OR (\"PolicySourceVersion\" >= 1 AND \"RetentionPolicyVersion\" >= 1)");
        }
    }
}
