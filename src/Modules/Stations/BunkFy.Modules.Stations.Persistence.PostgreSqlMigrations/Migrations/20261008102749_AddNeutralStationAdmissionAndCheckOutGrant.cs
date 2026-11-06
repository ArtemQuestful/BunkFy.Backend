using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Stations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddNeutralStationAdmissionAndCheckOutGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_browser_actor",
                schema: "stations",
                table: "browser_sessions");

            migrationBuilder.AddColumn<long>(
                name: "RegistrationRevision",
                schema: "stations",
                table: "browser_sessions",
                type: "bigint",
                nullable: true);

            // No legacy job revision is neutral identity evidence. Preserve credentials and registrations.
            // Lock both old actor kinds conservatively; the next PIN unlock observes the current registration.
            migrationBuilder.Sql("""
                UPDATE stations.browser_sessions
                SET "StaffMemberId" = NULL, "ActorSessionId" = NULL, "AuthorityKind" = 0,
                    "CredentialRevision" = NULL, "GrantRevision" = NULL, "RegistrationRevision" = NULL,
                    "ActorIdleExpiresAtUtc" = NULL, "ActorAbsoluteExpiresAtUtc" = NULL,
                    "Generation" = "Generation" + 1
                WHERE "StaffMemberId" IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "staff_check_out_grants",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_check_out_grants", x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
                    table.CheckConstraint("CK_check_out_grant_revision", "\"Revision\" > 0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 17 AND \"Outcome\" BETWEEN 1 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_browser_actor",
                schema: "stations",
                table: "browser_sessions",
                sql: "(\"StaffMemberId\" IS NULL AND \"ActorSessionId\" IS NULL AND \"AuthorityKind\" = 0 AND \"CredentialRevision\" IS NULL AND \"GrantRevision\" IS NULL AND \"RegistrationRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NULL) OR (\"StaffMemberId\" IS NOT NULL AND \"ActorSessionId\" IS NOT NULL AND \"AuthorityKind\" IN (1,2) AND \"CredentialRevision\" IS NOT NULL AND \"CredentialRevision\" > 0 AND \"RegistrationRevision\" IS NOT NULL AND \"RegistrationRevision\" > 0 AND \"GrantRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NOT NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Operational rollback retains additive schemas. Only an unused checkout extension may be removed.
            migrationBuilder.Sql("""
                DO $station_checkout$
                BEGIN
                    IF EXISTS (SELECT 1 FROM stations.staff_check_out_grants)
                       OR EXISTS (SELECT 1 FROM stations.operation_receipts WHERE "Kind" IN (16, 17)) THEN
                        RAISE EXCEPTION 'Used station checkout data must be retained; disable the feature and forward-fix.';
                    END IF;
                END $station_checkout$;
                """);
            migrationBuilder.DropTable(
                name: "staff_check_out_grants",
                schema: "stations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_browser_actor",
                schema: "stations",
                table: "browser_sessions");

            // No legacy job revision is neutral identity evidence. Preserve credentials and registrations.
            // Lock both old actor kinds conservatively; the next PIN unlock observes the current registration.
            migrationBuilder.Sql("""
                UPDATE stations.browser_sessions
                SET "StaffMemberId" = NULL, "ActorSessionId" = NULL, "AuthorityKind" = 0,
                    "CredentialRevision" = NULL, "GrantRevision" = NULL, "RegistrationRevision" = NULL,
                    "ActorIdleExpiresAtUtc" = NULL, "ActorAbsoluteExpiresAtUtc" = NULL,
                    "Generation" = "Generation" + 1
                WHERE "StaffMemberId" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "RegistrationRevision",
                schema: "stations",
                table: "browser_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receipt_kind",
                schema: "stations",
                table: "operation_receipts",
                sql: "\"Kind\" BETWEEN 1 AND 15 AND \"Outcome\" BETWEEN 1 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_browser_actor",
                schema: "stations",
                table: "browser_sessions",
                sql: "(\"StaffMemberId\" IS NULL AND \"ActorSessionId\" IS NULL AND \"AuthorityKind\" = 0 AND \"CredentialRevision\" IS NULL AND \"GrantRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NULL) OR (\"StaffMemberId\" IS NOT NULL AND \"ActorSessionId\" IS NOT NULL AND \"CredentialRevision\" IS NOT NULL AND \"CredentialRevision\" > 0 AND \"ActorIdleExpiresAtUtc\" IS NOT NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NOT NULL AND ((\"AuthorityKind\" = 1 AND \"GrantRevision\" IS NULL) OR (\"AuthorityKind\" = 2 AND \"GrantRevision\" IS NOT NULL AND \"GrantRevision\" > 0)))");
        }
    }
}
