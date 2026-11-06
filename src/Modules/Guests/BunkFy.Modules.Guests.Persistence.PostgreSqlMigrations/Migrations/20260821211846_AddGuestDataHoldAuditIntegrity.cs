using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestDataHoldAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_data_hold_receipts_data_holds_ScopeId_HoldId",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_data_holds_ScopeId_Id",
                schema: "guests",
                table: "data_holds");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_data_holds_receipt_evidence",
                schema: "guests",
                table: "data_holds",
                columns: new[] { "ScopeId", "Id", "PropertyId", "GuestId", "ReasonCode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_holds_audit_text",
                schema: "guests",
                table: "data_holds",
                sql: "length(trim(\"ReasonCode\")) > 0 AND \"ReasonCode\" = lower(trim(\"ReasonCode\")) AND length(trim(\"PlacedBy\")) > 0 AND \"PlacedBy\" = trim(\"PlacedBy\") AND (\"ReleasedBy\" IS NULL OR (length(trim(\"ReleasedBy\")) > 0 AND \"ReleasedBy\" = trim(\"ReleasedBy\")))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_holds_coordinates",
                schema: "guests",
                table: "data_holds",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_holds_timestamps",
                schema: "guests",
                table: "data_holds",
                sql: "\"PlacedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND (\"ReleasedAtUtc\" IS NULL OR \"ReleasedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00')");

            migrationBuilder.CreateIndex(
                name: "IX_guest_data_hold_receipts_hold_evidence",
                schema: "guests",
                table: "data_hold_receipts",
                columns: new[] { "ScopeId", "HoldId", "PropertyId", "GuestId", "ReasonCode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_hold_receipts_audit_text",
                schema: "guests",
                table: "data_hold_receipts",
                sql: "length(trim(\"ReasonCode\")) > 0 AND \"ReasonCode\" = lower(trim(\"ReasonCode\")) AND length(trim(\"ActorId\")) > 0 AND \"ActorId\" = trim(\"ActorId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_hold_receipts_coordinates",
                schema: "guests",
                table: "data_hold_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"IdempotencyKey\" <> '00000000-0000-0000-0000-000000000000' AND \"HoldId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_hold_receipts_timestamp",
                schema: "guests",
                table: "data_hold_receipts",
                sql: "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_data_hold_receipts_hold_evidence",
                schema: "guests",
                table: "data_hold_receipts",
                columns: new[] { "ScopeId", "HoldId", "PropertyId", "GuestId", "ReasonCode" },
                principalSchema: "guests",
                principalTable: "data_holds",
                principalColumns: new[] { "ScopeId", "Id", "PropertyId", "GuestId", "ReasonCode" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_data_hold_receipts_hold_evidence",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_data_holds_receipt_evidence",
                schema: "guests",
                table: "data_holds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_holds_audit_text",
                schema: "guests",
                table: "data_holds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_holds_coordinates",
                schema: "guests",
                table: "data_holds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_holds_timestamps",
                schema: "guests",
                table: "data_holds");

            migrationBuilder.DropIndex(
                name: "IX_guest_data_hold_receipts_hold_evidence",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_hold_receipts_audit_text",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_hold_receipts_coordinates",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_hold_receipts_timestamp",
                schema: "guests",
                table: "data_hold_receipts");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_data_holds_ScopeId_Id",
                schema: "guests",
                table: "data_holds",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_data_hold_receipts_data_holds_ScopeId_HoldId",
                schema: "guests",
                table: "data_hold_receipts",
                columns: new[] { "ScopeId", "HoldId" },
                principalSchema: "guests",
                principalTable: "data_holds",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
