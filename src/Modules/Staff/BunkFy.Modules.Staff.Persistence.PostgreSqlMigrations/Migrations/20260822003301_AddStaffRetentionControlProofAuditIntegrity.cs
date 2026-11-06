using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Staff.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffRetentionControlProofAuditIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_staff_retention_anonymisation_receipts_staff_members_ScopeI~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_retention_anonymisation_receipts_staff_retention_exec~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_staff_retention_sweep_checkpoints_ScopeId_Id",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_checkpoints_key",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_key",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_version",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_staff_retention_anonymisation_receipts_ScopeId_Id",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_staff_retention_anonymisation_receipts_ScopeId_ExecutionId_~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_receipts_deadline",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_receipts_digests",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "IX_staff_retention_sweep_checkpoints_ScopeId_DataClassKey_Exec~",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                newName: "UX_staff_retention_checkpoints_data_class_policy");

            migrationBuilder.RenameIndex(
                name: "IX_staff_retention_executions_ScopeId_DataClassKey_CompletedAt~",
                schema: "staff",
                table: "staff_retention_executions",
                newName: "IX_staff_retention_executions_history");

            migrationBuilder.RenameIndex(
                name: "IX_staff_retention_anonymisation_receipts_ScopeId_StaffMemberId",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                newName: "UX_staff_retention_receipts_staff_member");

            migrationBuilder.CreateIndex(
                name: "UX_staff_retention_checkpoints_last_execution",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "LastExecutionId" },
                unique: true,
                filter: "\"LastExecutionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_checkpoints_coordinates",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_checkpoints_key",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_checkpoints_lifecycle",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                sql: "(\"LastExecutionId\" IS NULL AND ((\"Version\" = 1 AND \"AfterProjectionOrdinal\" = 0) OR \"Version\" >= 3)) OR (\"LastExecutionId\" IS NOT NULL AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_checkpoints_timestamp",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                sql: "\"UpdatedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_coordinates",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_key",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_timestamp",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"StartedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_version",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");

            migrationBuilder.CreateIndex(
                name: "IX_staff_retention_receipts_execution",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "UX_staff_retention_receipts_event",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "EventId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_receipts_coordinates",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"ExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000' AND \"EventId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_receipts_digests",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                sql: "char_length(\"PolicyEvidenceSha256\") = 64 AND \"PolicyEvidenceSha256\" ~ '^[0-9a-f]+$' AND char_length(\"CanonicalSha256\") = 64 AND \"CanonicalSha256\" ~ '^[0-9a-f]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_receipts_timestamps",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                sql: "\"DepartedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND \"RetentionDeadlineUtc\" >= \"DepartedAtUtc\" AND \"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddForeignKey(
                name: "FK_staff_retention_receipts_execution",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "staff",
                principalTable: "staff_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_retention_receipts_staff_member",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "StaffMemberId" },
                principalSchema: "staff",
                principalTable: "staff_members",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Keep this provider-owned so two required relationships do not
            // share the receipt's StaffMemberId in EF change tracking.
            migrationBuilder.AddForeignKey(
                name: "FK_staff_retention_receipts_tombstone",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "StaffMemberId" },
                principalSchema: "staff",
                principalTable: "staff_anonymisation_tombstones",
                principalColumns: new[] { "ScopeId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_staff_retention_receipts_execution",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_retention_receipts_staff_member",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_retention_receipts_tombstone",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_staff_retention_checkpoints_last_execution",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_checkpoints_coordinates",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_checkpoints_key",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_checkpoints_lifecycle",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_checkpoints_timestamp",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_coordinates",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_key",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_timestamp",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_executions_version",
                schema: "staff",
                table: "staff_retention_executions");

            migrationBuilder.DropIndex(
                name: "IX_staff_retention_receipts_execution",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropIndex(
                name: "UX_staff_retention_receipts_event",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_receipts_coordinates",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_receipts_digests",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_retention_receipts_timestamps",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts");

            migrationBuilder.RenameIndex(
                name: "UX_staff_retention_checkpoints_data_class_policy",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                newName: "IX_staff_retention_sweep_checkpoints_ScopeId_DataClassKey_Exec~");

            migrationBuilder.RenameIndex(
                name: "IX_staff_retention_executions_history",
                schema: "staff",
                table: "staff_retention_executions",
                newName: "IX_staff_retention_executions_ScopeId_DataClassKey_CompletedAt~");

            migrationBuilder.RenameIndex(
                name: "UX_staff_retention_receipts_staff_member",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                newName: "IX_staff_retention_anonymisation_receipts_ScopeId_StaffMemberId");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_staff_retention_sweep_checkpoints_ScopeId_Id",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_staff_retention_anonymisation_receipts_ScopeId_Id",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_checkpoints_key",
                schema: "staff",
                table: "staff_retention_sweep_checkpoints",
                sql: "\"DataClassKey\" ~ '^[A-Za-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_key",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"DataClassKey\" ~ '^[A-Za-z0-9.-]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_executions_version",
                schema: "staff",
                table: "staff_retention_executions",
                sql: "\"Version\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_staff_retention_anonymisation_receipts_ScopeId_ExecutionId_~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId", "StaffMemberId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_receipts_deadline",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                sql: "\"RetentionDeadlineUtc\" >= \"DepartedAtUtc\" AND \"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_retention_receipts_digests",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                sql: "char_length(\"PolicyEvidenceSha256\") = 64 AND char_length(\"CanonicalSha256\") = 64");

            migrationBuilder.AddForeignKey(
                name: "FK_staff_retention_anonymisation_receipts_staff_members_ScopeI~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "StaffMemberId" },
                principalSchema: "staff",
                principalTable: "staff_members",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_retention_anonymisation_receipts_staff_retention_exec~",
                schema: "staff",
                table: "staff_retention_anonymisation_receipts",
                columns: new[] { "ScopeId", "ExecutionId" },
                principalSchema: "staff",
                principalTable: "staff_retention_executions",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
