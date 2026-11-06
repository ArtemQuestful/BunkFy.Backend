using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestAnonymisationProofAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_receipts_guest_profiles_ScopeId_GuestId",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_restore_receipts_guest_anonymisation_to~",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_tombstones_guest_profiles_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_receipt_digest",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_revision",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_anonymisation_restore_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropIndex(
                name: "IX_guest_anonymisation_restore_receipts_ScopeId_GuestId_Ledger~",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_versions",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_anonymisation_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_actor",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "IX_guest_anonymisation_receipts_ScopeId_IdempotencyKey",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "UX_guest_anonymisation_receipts_idempotency");

            migrationBuilder.RenameIndex(
                name: "IX_guest_anonymisation_receipts_ScopeId_GuestId_ResultingGuest~",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "UX_guest_anonymisation_receipts_guest_version");

            migrationBuilder.RenameIndex(
                name: "IX_guest_anonymisation_receipts_ScopeId_CaseId_ApprovalRevisio~",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "UX_guest_anonymisation_receipts_case_operation");

            migrationBuilder.CreateIndex(
                name: "UX_guest_anonymisation_tombstones_ledger_entry",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                columns: new[] { "ScopeId", "LedgerEntryId" },
                unique: true,
                filter: "\"LedgerEntryId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_coordinates",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_lifecycle",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "(\"Authority\" = 1 AND (\"Revision\" = 1 OR (\"Revision\" = 2 AND \"LedgerEntryId\" IS NOT NULL))) OR (\"Authority\" = 2 AND \"Revision\" = 1 AND \"LedgerEntryId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_receipt_digest",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "char_length(\"OwnerReceiptSha256\") = 64 AND \"OwnerReceiptSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_restore_pair",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "(\"LedgerEntryId\" IS NULL AND \"LastReplayedAtUtc\" IS NULL) OR (\"LedgerEntryId\" IS NOT NULL AND \"LedgerEntryId\" <> '00000000-0000-0000-0000-000000000000' AND \"LastReplayedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_revision",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "\"Revision\" BETWEEN 1 AND 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_timestamps",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND (\"LastReplayedAtUtc\" IS NULL OR \"LastReplayedAtUtc\" >= \"CompletedAtUtc\")");

            migrationBuilder.CreateIndex(
                name: "UX_guest_anonymisation_restore_receipts_tombstone",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_coordinates",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"LedgerEntryId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"OwnerReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "char_length(\"OwnerReceiptSha256\") = 64 AND \"OwnerReceiptSha256\" ~ '^[0-9a-f]+$' AND char_length(\"CanonicalSha256\") = 64 AND \"CanonicalSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_timestamp",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "\"ReplayedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_versions",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "\"OwnerReceiptContractVersion\" >= 1 AND \"ResultingGuestVersion\" >= 1 AND \"TombstoneRevision\" BETWEEN 1 AND 2");

            migrationBuilder.CreateIndex(
                name: "UX_guest_anonymisation_receipts_event",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                columns: new[] { "ScopeId", "EventId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_actor",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "length(\"ActorId\") > 0 AND \"ActorId\" = trim(\"ActorId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_coordinates",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"IdempotencyKey\" <> '00000000-0000-0000-0000-000000000000' AND \"RoutingPropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"CaseId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "char_length(\"ApprovalEvidenceSha256\") = 64 AND \"ApprovalEvidenceSha256\" ~ '^[0-9a-f]+$' AND char_length(\"PolicySetSha256\") = 64 AND \"PolicySetSha256\" ~ '^[0-9a-f]+$' AND char_length(\"CanonicalSha256\") = 64 AND \"CanonicalSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_timestamp",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_receipts_guest_profile",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_restore_receipts_tombstone",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_anonymisation_tombstones",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_tombstones_guest_profile",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                columns: new[] { "ScopeId", "Id" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_receipts_guest_profile",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_restore_receipts_tombstone",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_anonymisation_tombstones_guest_profile",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropIndex(
                name: "UX_guest_anonymisation_tombstones_ledger_entry",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_coordinates",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_lifecycle",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_receipt_digest",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_restore_pair",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_revision",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_timestamps",
                schema: "guests",
                table: "guest_anonymisation_tombstones");

            migrationBuilder.DropIndex(
                name: "UX_guest_anonymisation_restore_receipts_tombstone",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_coordinates",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_timestamp",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_versions",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts");

            migrationBuilder.DropIndex(
                name: "UX_guest_anonymisation_receipts_event",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_actor",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_coordinates",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_anonymisation_receipts_timestamp",
                schema: "guests",
                table: "guest_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "UX_guest_anonymisation_receipts_idempotency",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "IX_guest_anonymisation_receipts_ScopeId_IdempotencyKey");

            migrationBuilder.RenameIndex(
                name: "UX_guest_anonymisation_receipts_guest_version",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "IX_guest_anonymisation_receipts_ScopeId_GuestId_ResultingGuest~");

            migrationBuilder.RenameIndex(
                name: "UX_guest_anonymisation_receipts_case_operation",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                newName: "IX_guest_anonymisation_receipts_ScopeId_CaseId_ApprovalRevisio~");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_anonymisation_restore_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_anonymisation_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_receipt_digest",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "\"OwnerReceiptSha256\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_tombstones_revision",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                sql: "\"Revision\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_guest_anonymisation_restore_receipts_ScopeId_GuestId_Ledger~",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                columns: new[] { "ScopeId", "GuestId", "LedgerEntryId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "char_length(\"OwnerReceiptSha256\") = 64 AND char_length(\"CanonicalSha256\") = 64");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_restore_receipts_versions",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                sql: "\"OwnerReceiptContractVersion\" >= 1 AND \"ResultingGuestVersion\" >= 1 AND \"TombstoneRevision\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_actor",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "length(trim(\"ActorId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_anonymisation_receipts_digests",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                sql: "\"ApprovalEvidenceSha256\" ~ '^[0-9a-f]{64}$' AND \"PolicySetSha256\" ~ '^[0-9a-f]{64}$' AND \"CanonicalSha256\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_receipts_guest_profiles_ScopeId_GuestId",
                schema: "guests",
                table: "guest_anonymisation_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_restore_receipts_guest_anonymisation_to~",
                schema: "guests",
                table: "guest_anonymisation_restore_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_anonymisation_tombstones",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_anonymisation_tombstones_guest_profiles_ScopeId_Id",
                schema: "guests",
                table: "guest_anonymisation_tombstones",
                columns: new[] { "ScopeId", "Id" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
