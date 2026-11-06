using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AllowTenantTerminationExportAuditScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_export_audit_scope",
                schema: "data-rights",
                table: "export_audit_entries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_export_audit_scope",
                schema: "data-rights",
                table: "export_audit_entries",
                sql: "(\"CaseKind\" = 1 AND \"PropertyId\" IS NOT NULL) OR (\"CaseKind\" IN (2, 3) AND \"PropertyId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_export_audit_scope",
                schema: "data-rights",
                table: "export_audit_entries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_export_audit_scope",
                schema: "data-rights",
                table: "export_audit_entries",
                sql: "(\"CaseKind\" = 1 AND \"PropertyId\" IS NOT NULL) OR (\"CaseKind\" = 3 AND \"PropertyId\" IS NULL)");
        }
    }
}
