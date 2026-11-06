using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Staff.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffAuthoritativeStateIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_dates",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_versions",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_members_anonymised_profile",
                schema: "staff",
                table: "staff_members",
                sql: "\"Status\" <> 4 OR (\"DisplayName\" = 'Anonymised staff member' AND \"DisplayNameSearch\" = 'ANONYMISED STAFF MEMBER' AND \"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkEmailSearch\" IS NULL AND \"WorkPhone\" IS NULL AND \"WorkPhoneSearch\" IS NULL AND \"EmployeeNumber\" IS NULL AND \"EmployeeNumberSearch\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL AND \"AuthSubjectId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_members_coordinates",
                schema: "staff",
                table: "staff_members",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_members_optional_text",
                schema: "staff",
                table: "staff_members",
                sql: "(\"JobTitle\" IS NULL OR length(trim(\"JobTitle\")) > 0) AND (\"Department\" IS NULL OR length(trim(\"Department\")) > 0) AND (\"AuthSubjectId\" IS NULL OR length(trim(\"AuthSubjectId\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_members_search_shape",
                schema: "staff",
                table: "staff_members",
                sql: "length(trim(\"DisplayNameSearch\")) > 0 AND ((\"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL) OR (\"LegalName\" IS NOT NULL AND \"LegalNameSearch\" IS NOT NULL AND length(trim(\"LegalName\")) > 0 AND length(trim(\"LegalNameSearch\")) > 0)) AND ((\"WorkEmail\" IS NULL AND \"WorkEmailSearch\" IS NULL) OR (\"WorkEmail\" IS NOT NULL AND \"WorkEmailSearch\" IS NOT NULL AND length(trim(\"WorkEmail\")) > 0 AND length(trim(\"WorkEmailSearch\")) > 0)) AND ((\"WorkPhone\" IS NULL AND \"WorkPhoneSearch\" IS NULL) OR (\"WorkPhone\" IS NOT NULL AND \"WorkPhoneSearch\" IS NOT NULL AND length(trim(\"WorkPhone\")) > 0 AND length(trim(\"WorkPhoneSearch\")) > 0)) AND ((\"EmployeeNumber\" IS NULL AND \"EmployeeNumberSearch\" IS NULL) OR (\"EmployeeNumber\" IS NOT NULL AND \"EmployeeNumberSearch\" IS NOT NULL AND length(trim(\"EmployeeNumber\")) > 0 AND length(trim(\"EmployeeNumberSearch\")) > 0))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_members_timestamps",
                schema: "staff",
                table: "staff_members",
                sql: "\"LastChangedAtUtc\" >= \"CreatedAtUtc\" AND (\"SuspendedAtUtc\" IS NULL OR (\"SuspendedAtUtc\" >= \"CreatedAtUtc\" AND \"SuspendedAtUtc\" <= \"LastChangedAtUtc\")) AND (\"DepartedAtUtc\" IS NULL OR (\"DepartedAtUtc\" >= \"CreatedAtUtc\" AND \"DepartedAtUtc\" <= \"LastChangedAtUtc\")) AND (\"AnonymisedAtUtc\" IS NULL OR (\"DepartedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"DepartedAtUtc\" AND \"AnonymisedAtUtc\" <= \"LastChangedAtUtc\"))");

            migrationBuilder.CreateIndex(
                name: "UX_staff_assignments_current_primary",
                schema: "staff",
                table: "property_assignments",
                columns: new[] { "ScopeId", "StaffMemberId" },
                unique: true,
                filter: "\"IsCurrent\" AND \"IsPrimary\"");

            migrationBuilder.CreateIndex(
                name: "UX_staff_assignments_current_property",
                schema: "staff",
                table: "property_assignments",
                columns: new[] { "ScopeId", "StaffMemberId", "PropertyId" },
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_actor_shape",
                schema: "staff",
                table: "property_assignments",
                sql: "length(trim(\"AssignedBy\")) > 0 AND (\"UnassignedBy\" IS NULL OR length(trim(\"UnassignedBy\")) > 0) AND (\"UnassignmentReason\" IS NULL OR length(trim(\"UnassignmentReason\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_coordinates",
                schema: "staff",
                table: "property_assignments",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_dates",
                schema: "staff",
                table: "property_assignments",
                sql: "\"EffectiveFrom\" > DATE '0001-01-01' AND (\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_timestamps",
                schema: "staff",
                table: "property_assignments",
                sql: "\"UnassignedAtUtc\" IS NULL OR \"UnassignedAtUtc\" >= \"AssignedAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_versions",
                schema: "staff",
                table: "property_assignments",
                sql: "\"AssignedAtVersion\" >= 2 AND (\"UnassignedAtVersion\" IS NULL OR \"UnassignedAtVersion\" > \"AssignedAtVersion\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_members_anonymised_profile",
                schema: "staff",
                table: "staff_members");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_members_coordinates",
                schema: "staff",
                table: "staff_members");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_members_optional_text",
                schema: "staff",
                table: "staff_members");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_members_search_shape",
                schema: "staff",
                table: "staff_members");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_members_timestamps",
                schema: "staff",
                table: "staff_members");

            migrationBuilder.DropIndex(
                name: "UX_staff_assignments_current_primary",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropIndex(
                name: "UX_staff_assignments_current_property",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_actor_shape",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_coordinates",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_dates",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_timestamps",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_assignments_versions",
                schema: "staff",
                table: "property_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_dates",
                schema: "staff",
                table: "property_assignments",
                sql: "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_assignments_versions",
                schema: "staff",
                table: "property_assignments",
                sql: "\"AssignedAtVersion\" >= 2 AND (\"UnassignedAtVersion\" IS NULL OR \"UnassignedAtVersion\" >= \"AssignedAtVersion\")");
        }
    }
}
