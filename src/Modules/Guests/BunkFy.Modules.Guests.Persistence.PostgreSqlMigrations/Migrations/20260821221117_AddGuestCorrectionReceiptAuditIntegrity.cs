using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestCorrectionReceiptAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_data_rights_correction_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropIndex(
                name: "IX_guest_data_rights_correction_receipts_ScopeId_GuestId_Curre~",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropIndex(
                name: "IX_guest_data_rights_correction_receipts_ScopeId_PropertyId_Ca~",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.CreateIndex(
                name: "UX_guest_correction_receipts_case_approval",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "PropertyId", "CaseId", "ApprovalRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_guest_correction_receipts_guest_version",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "GuestId", "CurrentRecordVersion" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_coordinates",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"IdempotencyKey\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"CaseId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND \"CompletionEventId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> \"CompletionEventId\" AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_timestamp",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_data_rights_correction_receipts_guest_profile",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_data_rights_correction_receipts_guest_profile",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropIndex(
                name: "UX_guest_correction_receipts_case_approval",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropIndex(
                name: "UX_guest_correction_receipts_guest_version",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_coordinates",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_timestamp",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_data_rights_correction_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_guest_data_rights_correction_receipts_ScopeId_GuestId_Curre~",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "GuestId", "CurrentRecordVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_guest_data_rights_correction_receipts_ScopeId_PropertyId_Ca~",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                columns: new[] { "ScopeId", "PropertyId", "CaseId", "ApprovalRevision" });
        }
    }
}
