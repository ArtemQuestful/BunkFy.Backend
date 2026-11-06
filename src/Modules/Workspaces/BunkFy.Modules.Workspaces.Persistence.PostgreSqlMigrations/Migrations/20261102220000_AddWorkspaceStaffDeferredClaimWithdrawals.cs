using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Workspaces.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceStaffDeferredClaimWithdrawals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_deferred_claim_withdrawals",
                schema: "workspaces",
                columns: table => new
                {
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnrollmentLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimVersion = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_deferred_claim_withdrawals", x => x.ClaimId);
                    table.CheckConstraint("CK_staff_deferred_claim_withdrawal_coordinates", "\"ClaimId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"EnrollmentLinkId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"EventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ScopeId\" = \"OrganizationId\"::text");
                    table.CheckConstraint("CK_staff_deferred_claim_withdrawal_version", "\"ClaimVersion\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_staff_deferred_claim_withdrawals_ScopeId_EnrollmentLinkId_C~",
                schema: "workspaces",
                table: "staff_deferred_claim_withdrawals",
                columns: new[] { "ScopeId", "EnrollmentLinkId", "ClaimVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_deferred_claim_withdrawals_ScopeId_ClaimId",
                schema: "workspaces",
                table: "staff_deferred_claim_withdrawals",
                columns: new[] { "ScopeId", "ClaimId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    LOCK TABLE workspaces.staff_deferred_claim_withdrawals
                        IN ACCESS EXCLUSIVE MODE;
                    IF EXISTS (
                        SELECT 1
                        FROM workspaces.staff_deferred_claim_withdrawals
                    ) THEN
                        RAISE EXCEPTION 'Cannot remove durable Staff claim withdrawals while deferred observations exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "staff_deferred_claim_withdrawals",
                schema: "workspaces");
        }
    }
}
