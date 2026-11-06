using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestProcessingRestrictionAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_processing_restrictions_ScopeId_Id",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_lifecycle",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_effective_state",
                schema: "guests",
                table: "guest_processing_restriction_state");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_processing_restriction_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_versions",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_processing_restrictions_receipt_evidence",
                schema: "guests",
                table: "guest_processing_restrictions",
                columns: new[] { "ScopeId", "Id", "PropertyId", "GuestId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_audit_text",
                schema: "guests",
                table: "guest_processing_restrictions",
                sql: "length(trim(\"AppliedBy\")) > 0 AND \"AppliedBy\" = trim(\"AppliedBy\") AND (\"ReleasedBy\" IS NULL OR (length(trim(\"ReleasedBy\")) > 0 AND \"ReleasedBy\" = trim(\"ReleasedBy\")))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_coordinates",
                schema: "guests",
                table: "guest_processing_restrictions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"ApplyCaseId\" <> '00000000-0000-0000-0000-000000000000' AND (\"ReleaseCaseId\" IS NULL OR \"ReleaseCaseId\" <> '00000000-0000-0000-0000-000000000000') AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_lifecycle",
                schema: "guests",
                table: "guest_processing_restrictions",
                sql: "(\"Status\" = 1 AND \"ReleaseCaseId\" IS NULL AND \"ReleaseApprovalRevision\" IS NULL AND \"ReleaseSelectedGuestVersion\" IS NULL AND \"ReleasedBy\" IS NULL AND \"ReleasedAtUtc\" IS NULL AND \"Version\" = 1) OR (\"Status\" = 2 AND \"ReleaseCaseId\" IS NOT NULL AND \"ReleaseCaseId\" <> \"ApplyCaseId\" AND \"ReleaseApprovalRevision\" >= 1 AND \"ReleaseSelectedGuestVersion\" >= 1 AND \"ReleasedBy\" IS NOT NULL AND \"ReleasedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" >= \"AppliedAtUtc\" AND \"Version\" = 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_timestamps",
                schema: "guests",
                table: "guest_processing_restrictions",
                sql: "\"AppliedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND (\"ReleasedAtUtc\" IS NULL OR \"ReleasedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_effective_state",
                schema: "guests",
                table: "guest_processing_restriction_state",
                sql: "\"ActiveRestrictionCount\" >= 0 AND \"Revision\" >= \"ActiveRestrictionCount\" AND MOD(\"Revision\" - \"ActiveRestrictionCount\", 2) = 0 AND ((\"ActiveRestrictionCount\" = 0 AND NOT \"IsRestricted\") OR (\"ActiveRestrictionCount\" > 0 AND \"IsRestricted\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_projection_coordinates",
                schema: "guests",
                table: "guest_processing_restriction_state",
                sql: "\"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_projection_ordinal",
                schema: "guests",
                table: "guest_processing_restriction_state",
                sql: "\"ProjectionOrdinal\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_transition_timestamp",
                schema: "guests",
                table: "guest_processing_restriction_state",
                sql: "\"LastTransitionAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.CreateIndex(
                name: "IX_guest_processing_restriction_receipts_restriction_evidence",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                columns: new[] { "ScopeId", "RestrictionId", "PropertyId", "GuestId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_audit_text",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                sql: "length(trim(\"ActorId\")) > 0 AND \"ActorId\" = trim(\"ActorId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_coordinates",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"IdempotencyKey\" <> '00000000-0000-0000-0000-000000000000' AND \"RestrictionId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"CaseId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_timestamp",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                sql: "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_versions",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                sql: "\"ApprovalRevision\" >= 1 AND \"SelectedGuestVersion\" >= 1 AND \"ResultingProjectionRevision\" >= 1 AND ((\"Action\" = 1 AND \"ResultingRestrictionVersion\" = 1 AND \"EffectiveRestricted\") OR (\"Action\" = 2 AND \"ResultingRestrictionVersion\" = 2))");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_processing_restriction_receipts_restriction_evidence",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                columns: new[] { "ScopeId", "RestrictionId", "PropertyId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_processing_restrictions",
                principalColumns: new[] { "ScopeId", "Id", "PropertyId", "GuestId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_processing_restrictions_effective_projection",
                schema: "guests",
                table: "guest_processing_restrictions",
                columns: new[] { "ScopeId", "PropertyId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_processing_restriction_state",
                principalColumns: new[] { "ScopeId", "PropertyId", "GuestId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_processing_restriction_receipts_restriction_evidence",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_processing_restrictions_effective_projection",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_processing_restrictions_receipt_evidence",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_audit_text",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_coordinates",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_lifecycle",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_timestamps",
                schema: "guests",
                table: "guest_processing_restrictions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_effective_state",
                schema: "guests",
                table: "guest_processing_restriction_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_projection_coordinates",
                schema: "guests",
                table: "guest_processing_restriction_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_projection_ordinal",
                schema: "guests",
                table: "guest_processing_restriction_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restrictions_transition_timestamp",
                schema: "guests",
                table: "guest_processing_restriction_state");

            migrationBuilder.DropIndex(
                name: "IX_guest_processing_restriction_receipts_restriction_evidence",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_audit_text",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_coordinates",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_timestamp",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_versions",
                schema: "guests",
                table: "guest_processing_restriction_receipts");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_processing_restrictions_ScopeId_Id",
                schema: "guests",
                table: "guest_processing_restrictions",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_processing_restriction_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_lifecycle",
                schema: "guests",
                table: "guest_processing_restrictions",
                sql: "(\"Status\" = 1 AND \"ReleaseCaseId\" IS NULL AND \"ReleaseApprovalRevision\" IS NULL AND \"ReleaseSelectedGuestVersion\" IS NULL AND \"ReleasedBy\" IS NULL AND \"ReleasedAtUtc\" IS NULL AND \"Version\" = 1) OR (\"Status\" = 2 AND \"ReleaseCaseId\" IS NOT NULL AND \"ReleaseApprovalRevision\" >= 1 AND \"ReleaseSelectedGuestVersion\" >= 1 AND \"ReleasedBy\" IS NOT NULL AND \"ReleasedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" >= \"AppliedAtUtc\" AND \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restrictions_effective_state",
                schema: "guests",
                table: "guest_processing_restriction_state",
                sql: "(\"ActiveRestrictionCount\" = 0 AND NOT \"IsRestricted\") OR (\"ActiveRestrictionCount\" > 0 AND \"IsRestricted\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_processing_restriction_receipts_versions",
                schema: "guests",
                table: "guest_processing_restriction_receipts",
                sql: "\"ApprovalRevision\" >= 1 AND \"SelectedGuestVersion\" >= 1 AND \"ResultingProjectionRevision\" >= 1 AND ((\"Action\" = 1 AND \"ResultingRestrictionVersion\" = 1 AND \"EffectiveRestricted\") OR (\"Action\" = 2 AND \"ResultingRestrictionVersion\" >= 2))");
        }
    }
}
