using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddDataRightsExportRetryVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastRetryBaseVersion",
                schema: "data-rights",
                table: "export_artifacts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_export_artifacts_retry_version",
                schema: "data-rights",
                table: "export_artifacts",
                sql: "\"LastRetryBaseVersion\" IS NULL OR (\"LastRetryBaseVersion\" >= 1 AND \"LastRetryBaseVersion\" < \"Version\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_export_artifacts_retry_version",
                schema: "data-rights",
                table: "export_artifacts");

            migrationBuilder.DropColumn(
                name: "LastRetryBaseVersion",
                schema: "data-rights",
                table: "export_artifacts");
        }
    }
}
