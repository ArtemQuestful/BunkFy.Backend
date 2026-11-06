using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestRetentionControlProofAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_retention_anonymisation_receipts_guest_profiles_Scope~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_retention_anonymisation_receipts_guest_retention_exec~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_retention_sweep_checkpoints_ScopeId_Id",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_key",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_key",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_state",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_version",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guest_retention_anonymisation_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_guest_retention_anonymisation_receipts_ScopeId_ExecutionId_~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_actor",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_deadline",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_digests",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "IX_guest_retention_sweep_checkpoints_ScopeId_DataClassKey",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                newName: "UX_guest_retention_checkpoints_data_class");

            migrationBuilder.RenameIndex(
                name: "IX_guest_retention_executions_ScopeId_DataClassKey_CompletedAt~",
                schema: "guests",
                table: "guest_retention_executions",
                newName: "IX_guest_retention_executions_history");

            migrationBuilder.RenameIndex(
                name: "IX_guest_retention_anonymisation_receipts_ScopeId_GuestId",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                newName: "UX_guest_retention_receipts_guest");

            migrationBuilder.CreateIndex(
                name: "UX_guest_retention_checkpoints_last_execution",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "LastExecutionId" },
                unique: true,
                filter: "\"LastExecutionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_coordinates",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_key",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "(\"LastExecutionId\" IS NULL AND \"AfterProjectionOrdinal\" = 0 AND \"Version\" = 1) OR (\"LastExecutionId\" IS NOT NULL AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_timestamp",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "\"UpdatedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_coordinates",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_key",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_state",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "(\"State\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"ScannedCount\" IS NULL AND \"RemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL AND \"HoldReviewDueAtUtc\" IS NULL) OR (\"State\" IN (2, 3, 4) AND \"CompletedAtUtc\" BETWEEN \"StartedAtUtc\" AND \"DeadlineUtc\" AND \"ScannedCount\" IS NOT NULL AND \"RemainingCount\" IS NOT NULL AND \"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$' AND ((\"State\" = 3 AND \"HoldReviewDueAtUtc\" IS NOT NULL) OR (\"State\" <> 3 AND \"HoldReviewDueAtUtc\" IS NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_timestamp",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"StartedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_version",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");

            migrationBuilder.CreateIndex(
                name: "IX_guest_retention_receipts_execution",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "UX_guest_retention_receipts_event",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "EventId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_actor",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"ActorId\" = 'system:retention'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_coordinates",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"ExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"GuestId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_digests",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "char_length(\"PolicySetSha256\") = 64 AND \"PolicySetSha256\" ~ '^[0-9a-f]+$' AND char_length(\"CanonicalSha256\") = 64 AND \"CanonicalSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_timestamps",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"RetentionDeadlineUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND \"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_retention_receipts_execution",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "guests",
                principalTable: "guest_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_retention_receipts_guest_profile",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_retention_receipts_tombstone",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_anonymisation_tombstones",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_guest_retention_receipts_execution",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_retention_receipts_guest_profile",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_guest_retention_receipts_tombstone",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_guest_retention_checkpoints_last_execution",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_coordinates",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_key",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_timestamp",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_coordinates",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_key",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_state",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_timestamp",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_version",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropIndex(
                name: "IX_guest_retention_receipts_execution",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_guest_retention_receipts_event",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_actor",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_coordinates",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_digests",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_timestamps",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "UX_guest_retention_checkpoints_data_class",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                newName: "IX_guest_retention_sweep_checkpoints_ScopeId_DataClassKey");

            migrationBuilder.RenameIndex(
                name: "IX_guest_retention_executions_history",
                schema: "guests",
                table: "guest_retention_executions",
                newName: "IX_guest_retention_executions_ScopeId_DataClassKey_CompletedAt~");

            migrationBuilder.RenameIndex(
                name: "UX_guest_retention_receipts_guest",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                newName: "IX_guest_retention_anonymisation_receipts_ScopeId_GuestId");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_retention_sweep_checkpoints_ScopeId_Id",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guest_retention_anonymisation_receipts_ScopeId_Id",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_key",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "length(trim(\"DataClassKey\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_key",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "length(trim(\"DataClassKey\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_state",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "(\"State\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"ScannedCount\" IS NULL AND \"RemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL AND \"HoldReviewDueAtUtc\" IS NULL) OR (\"State\" IN (2, 3, 4) AND \"CompletedAtUtc\" BETWEEN \"StartedAtUtc\" AND \"DeadlineUtc\" AND \"ScannedCount\" IS NOT NULL AND \"RemainingCount\" IS NOT NULL AND \"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$' AND ((\"State\" = 3 AND \"HoldReviewDueAtUtc\" IS NOT NULL) OR (\"State\" <> 3 AND \"HoldReviewDueAtUtc\" IS NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_version",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"Version\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_guest_retention_anonymisation_receipts_ScopeId_ExecutionId_~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId", "GuestId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_actor",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "length(trim(\"ActorId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_deadline",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_digests",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"PolicySetSha256\" ~ '^[0-9a-f]{64}$' AND \"CanonicalSha256\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddForeignKey(
                name: "FK_guest_retention_anonymisation_receipts_guest_profiles_Scope~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "GuestId" },
                principalSchema: "guests",
                principalTable: "guest_profiles",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guest_retention_anonymisation_receipts_guest_retention_exec~",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "guests",
                principalTable: "guest_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
