using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Workspaces.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacePropertyAuthorityTenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_staff_access_plan_properties_coordinates",
                schema: "workspaces",
                table: "staff_access_plan_properties",
                sql: "\"PlanId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_property_projection_coordinates",
                schema: "workspaces",
                table: "property_projection",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_property_projection_name",
                schema: "workspaces",
                table: "property_projection",
                sql: "\"Name\" IS NULL OR (char_length(\"Name\") > 0 AND \"Name\" = btrim(\"Name\") AND \"Name\" !~ '[[:cntrl:]]')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_property_projection_state",
                schema: "workspaces",
                table: "property_projection",
                sql: "\"Status\" IN (1, 2) AND (\"Status\" = 2 OR \"Name\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_staff_access_plan_properties_coordinates",
                schema: "workspaces",
                table: "staff_access_plan_properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_property_projection_coordinates",
                schema: "workspaces",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_property_projection_name",
                schema: "workspaces",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_property_projection_state",
                schema: "workspaces",
                table: "property_projection");
        }
    }
}
