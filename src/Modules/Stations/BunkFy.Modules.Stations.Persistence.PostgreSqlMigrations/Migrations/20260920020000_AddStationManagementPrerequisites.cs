using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Stations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStationManagementPrerequisites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssuranceExpiresAtUtc",
                schema: "stations",
                table: "setup_grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IssuerKind",
                schema: "stations",
                table: "setup_grants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IssuerSubjectId",
                schema: "stations",
                table: "setup_grants",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BrowserSessionId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IssuerKind",
                schema: "stations",
                table: "operation_receipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "IssuerSessionId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuerSubjectId",
                schema: "stations",
                table: "operation_receipts",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResourceVersion",
                schema: "stations",
                table: "operation_receipts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SetupGrantId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StaffMemberId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StationId",
                schema: "stations",
                table: "operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "staff_registrations",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    RosterReference = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_registrations", x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
                    table.CheckConstraint("CK_registration_version", "\"Version\" > 0 AND \"RosterReference\" > 0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_setup_issuer",
                schema: "stations",
                table: "setup_grants",
                sql: "(\"IssuerKind\" = 0 AND \"IssuerSubjectId\" IS NULL AND \"AssuranceExpiresAtUtc\" IS NULL) OR (\"IssuerKind\" IN (1,2) AND \"IssuerSubjectId\" IS NOT NULL AND length(\"IssuerSubjectId\") = 36 AND \"AssuranceExpiresAtUtc\" IS NOT NULL AND \"AssuranceExpiresAtUtc\" >= \"ExpiresAtUtc\" AND (\"IssuerKind\" = 1 OR (\"ExpectedEnrollmentAuthorityKind\" = 1 AND \"IssuerSubjectId\" = \"ExpectedEnrollmentAuthSubjectId\")))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_issuer",
                schema: "stations",
                table: "operation_receipts",
                sql: "(\"IssuerKind\" = 0 AND \"IssuerSubjectId\" IS NULL) OR (\"IssuerKind\" IN (1,2) AND \"IssuerSubjectId\" IS NOT NULL AND length(\"IssuerSubjectId\") = 36)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 15 AND \"Outcome\" BETWEEN 1 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_session",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"IssuerSessionId\" IS NULL OR (\"IssuerKind\" IN (1,2) AND \"Kind\" IN (1,9,15) AND \"IssuerSessionId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_own_pin",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" <> 15 OR (\"IssuerKind\" = 2 AND \"IssuerSubjectId\" IS NOT NULL AND \"IssuerSessionId\" IS NOT NULL AND \"IssuerSessionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"StaffMemberId\" IS NOT NULL AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ResourceVersion\" IS NOT NULL AND \"ResourceVersion\" >= 0 AND (\"Outcome\" <> 1 OR \"ResourceVersion\" > 0))");

            migrationBuilder.CreateIndex(
                name: "IX_staff_registrations_ScopeId_PropertyId_RosterReference",
                schema: "stations",
                table: "staff_registrations",
                columns: new[] { "ScopeId", "PropertyId", "RosterReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_registrations",
                schema: "stations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_setup_issuer",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_issuer",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_session",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_own_pin",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "AssuranceExpiresAtUtc",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropColumn(
                name: "IssuerKind",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropColumn(
                name: "IssuerSubjectId",
                schema: "stations",
                table: "setup_grants");

            migrationBuilder.DropColumn(
                name: "BrowserSessionId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "IssuerKind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "IssuerSessionId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "IssuerSubjectId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "ResourceVersion",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "SetupGrantId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "StaffMemberId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "StationId",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 8 AND \"Outcome\" BETWEEN 1 AND 5");
        }
    }
}
