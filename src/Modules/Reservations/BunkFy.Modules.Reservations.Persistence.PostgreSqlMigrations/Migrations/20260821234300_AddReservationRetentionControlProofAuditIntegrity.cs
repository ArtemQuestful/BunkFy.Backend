using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Reservations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationRetentionControlProofAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reservation_retention_anonymisation_receipts_reservation_re~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_reservation_retention_anonymisation_receipts_reservations_S~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_reservation_retention_sweep_checkpoints_ScopeId_Id",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_checkpoints_key",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_key",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_version",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_reservation_retention_anonymisation_receipts_ScopeId_Id",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_reservation_retention_anonymisation_receipts_ScopeId_Execut~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_receipts_deadline",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_receipts_digests",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "IX_reservation_retention_sweep_checkpoints_ScopeId_DataClassKe~",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                newName: "UX_reservation_retention_checkpoints_data_class_policy");

            migrationBuilder.RenameIndex(
                name: "IX_reservation_retention_executions_ScopeId_DataClassKey_Compl~",
                schema: "reservations",
                table: "reservation_retention_executions",
                newName: "IX_reservation_retention_executions_history");

            migrationBuilder.RenameIndex(
                name: "IX_reservation_retention_anonymisation_receipts_ScopeId_Reserv~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                newName: "UX_reservation_retention_receipts_reservation");

            migrationBuilder.CreateIndex(
                name: "UX_reservation_retention_checkpoints_last_execution",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "LastExecutionId" },
                unique: true,
                filter: "\"LastExecutionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_checkpoints_coordinates",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_checkpoints_key",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_checkpoints_lifecycle",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                sql: "(\"LastExecutionId\" IS NULL AND ((\"Version\" = 1 AND \"AfterProjectionOrdinal\" = 0) OR \"Version\" >= 3)) OR (\"LastExecutionId\" IS NOT NULL AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_checkpoints_timestamp",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                sql: "\"UpdatedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_coordinates",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_key",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_timestamp",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"StartedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_version",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_retention_receipts_execution",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "UX_reservation_retention_receipts_event",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "EventId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_receipts_coordinates",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"ExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_receipts_digests",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                sql: "char_length(\"PolicyEvidenceSha256\") = 64 AND \"PolicyEvidenceSha256\" ~ '^[0-9a-f]+$' AND char_length(\"CanonicalSha256\") = 64 AND \"CanonicalSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_receipts_timestamps",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                sql: "\"TerminalAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND \"RetentionDeadlineUtc\" >= \"TerminalAtUtc\" AND \"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddForeignKey(
                name: "FK_reservation_retention_receipts_execution",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "reservations",
                principalTable: "reservation_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reservation_retention_receipts_reservation",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ReservationId" },
                principalSchema: "reservations",
                principalTable: "reservations",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reservation_retention_receipts_tombstone",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ReservationId" },
                principalSchema: "reservations",
                principalTable: "reservation_anonymisation_tombstones",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reservation_retention_receipts_execution",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_reservation_retention_receipts_reservation",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_reservation_retention_receipts_tombstone",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_reservation_retention_checkpoints_last_execution",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_checkpoints_coordinates",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_checkpoints_key",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_checkpoints_lifecycle",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_checkpoints_timestamp",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_coordinates",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_key",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_timestamp",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_executions_version",
                schema: "reservations",
                table: "reservation_retention_executions");

            migrationBuilder.DropIndex(
                name: "IX_reservation_retention_receipts_execution",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_reservation_retention_receipts_event",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_receipts_coordinates",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_receipts_digests",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_retention_receipts_timestamps",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "UX_reservation_retention_checkpoints_data_class_policy",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                newName: "IX_reservation_retention_sweep_checkpoints_ScopeId_DataClassKe~");

            migrationBuilder.RenameIndex(
                name: "IX_reservation_retention_executions_history",
                schema: "reservations",
                table: "reservation_retention_executions",
                newName: "IX_reservation_retention_executions_ScopeId_DataClassKey_Compl~");

            migrationBuilder.RenameIndex(
                name: "UX_reservation_retention_receipts_reservation",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                newName: "IX_reservation_retention_anonymisation_receipts_ScopeId_Reserv~");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_reservation_retention_sweep_checkpoints_ScopeId_Id",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_reservation_retention_anonymisation_receipts_ScopeId_Id",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_checkpoints_key",
                schema: "reservations",
                table: "reservation_retention_sweep_checkpoints",
                sql: "\"DataClassKey\" ~ '^[A-Za-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_key",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"DataClassKey\" ~ '^[A-Za-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_executions_version",
                schema: "reservations",
                table: "reservation_retention_executions",
                sql: "\"Version\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_retention_anonymisation_receipts_ScopeId_Execut~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId", "ReservationId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_receipts_deadline",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                sql: "\"RetentionDeadlineUtc\" >= \"TerminalAtUtc\" AND \"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_retention_receipts_digests",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                sql: "char_length(\"PolicyEvidenceSha256\") = 64 AND char_length(\"CanonicalSha256\") = 64");

            migrationBuilder.AddForeignKey(
                name: "FK_reservation_retention_anonymisation_receipts_reservation_re~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "reservations",
                principalTable: "reservation_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reservation_retention_anonymisation_receipts_reservations_S~",
                schema: "reservations",
                table: "reservation_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ReservationId" },
                principalSchema: "reservations",
                principalTable: "reservations",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
