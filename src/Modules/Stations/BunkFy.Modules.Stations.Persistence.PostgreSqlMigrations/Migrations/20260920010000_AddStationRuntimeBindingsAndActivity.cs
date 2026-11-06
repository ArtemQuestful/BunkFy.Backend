using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Stations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStationRuntimeBindingsAndActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.AddColumn<string>(
                name: "EnrollmentAuthSubjectId",
                schema: "stations",
                table: "staff_credentials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrollmentAuthorityKind",
                schema: "stations",
                table: "staff_credentials",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedEnrollmentAuthSubjectId",
                schema: "stations",
                table: "setup_grants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedEnrollmentAuthorityKind",
                schema: "stations",
                table: "setup_grants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_credential_enrollment",
                schema: "stations",
                table: "staff_credentials",
                sql: "(\"EnrollmentAuthorityKind\" IN (0,2) AND \"EnrollmentAuthSubjectId\" IS NULL) OR (\"EnrollmentAuthorityKind\" = 1 AND \"EnrollmentAuthSubjectId\" IS NOT NULL AND length(btrim(\"EnrollmentAuthSubjectId\")) > 0 AND \"EnrollmentAuthSubjectId\" = btrim(\"EnrollmentAuthSubjectId\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_setup_enrollment",
                schema: "stations",
                table: "setup_grants",
                sql: "(\"ExpectedEnrollmentAuthorityKind\" = 0 AND \"ExpectedEnrollmentAuthSubjectId\" IS NULL) OR (\"ExpectedEnrollmentAuthorityKind\" = \"AuthorityKind\" AND ((\"ExpectedEnrollmentAuthorityKind\" = 2 AND \"ExpectedEnrollmentAuthSubjectId\" IS NULL) OR (\"ExpectedEnrollmentAuthorityKind\" = 1 AND \"ExpectedEnrollmentAuthSubjectId\" IS NOT NULL AND length(btrim(\"ExpectedEnrollmentAuthSubjectId\")) > 0 AND \"ExpectedEnrollmentAuthSubjectId\" = btrim(\"ExpectedEnrollmentAuthSubjectId\"))))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 8 AND \"Outcome\" BETWEEN 1 AND 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_credential_enrollment",
                schema: "stations",
                table: "staff_credentials");

            migrationBuilder.DropCheckConstraint(
                name: "CK_setup_enrollment",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "EnrollmentAuthSubjectId",
                schema: "stations",
                table: "staff_credentials");

            migrationBuilder.DropColumn(
                name: "EnrollmentAuthorityKind",
                schema: "stations",
                table: "staff_credentials");

            migrationBuilder.DropColumn(
                name: "ExpectedEnrollmentAuthSubjectId",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropColumn(
                name: "ExpectedEnrollmentAuthorityKind",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 7 AND \"Outcome\" BETWEEN 1 AND 5");
        }
    }
}
