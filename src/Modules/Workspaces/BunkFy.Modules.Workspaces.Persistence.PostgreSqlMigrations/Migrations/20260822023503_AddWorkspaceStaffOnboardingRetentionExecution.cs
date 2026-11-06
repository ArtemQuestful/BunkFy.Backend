using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Workspaces.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceStaffOnboardingRetentionExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations");

            migrationBuilder.Sql(
                """
                UPDATE workspaces.tenant_destroy_operations
                SET "Stage" = 21
                WHERE "Stage" = 20;
                """);

            migrationBuilder.CreateTable(
                name: "staff_onboarding_retention_executions",
                schema: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataClassKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExecutionPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AffectedCount = table.Column<int>(type: "integer", nullable: false),
                    ScannedCount = table.Column<int>(type: "integer", nullable: false),
                    RemainingCount = table.Column<int>(type: "integer", nullable: true),
                    OutcomeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_onboarding_retention_executions", x => x.Id);
                    table.UniqueConstraint("AK_staff_onboarding_retention_executions_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_coordinate", "char_length(trim(\"ScopeId\")) > 0 AND char_length(\"DataClassKey\") BETWEEN 1 AND 64 AND lower(\"DataClassKey\") = \"DataClassKey\" AND \"ExecutionPolicyVersion\" > 0 AND \"Attempt\" > 0");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_counts", "\"AffectedCount\" >= 0 AND \"ScannedCount\" >= \"AffectedCount\" AND (\"RemainingCount\" IS NULL OR \"RemainingCount\" >= 0)");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_failed_remaining", "\"State\" <> 3 OR \"RemainingCount\" > 0");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_lifecycle", "((\"State\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"RemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL) OR (\"State\" IN (2, 3) AND \"CompletedAtUtc\" IS NOT NULL AND \"RemainingCount\" IS NOT NULL AND \"OutcomeCode\" IS NOT NULL))");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_state", "\"State\" BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_timing", "\"StartedAtUtc\" <> '-infinity' AND \"DeadlineUtc\" > \"StartedAtUtc\" AND (\"CompletedAtUtc\" IS NULL OR (\"CompletedAtUtc\" >= \"StartedAtUtc\" AND \"CompletedAtUtc\" <= \"DeadlineUtc\"))");
                    table.CheckConstraint("CK_staff_onboarding_retention_execution_version", "\"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 21 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_retention_executions_history",
                schema: "workspaces",
                table: "staff_onboarding_retention_executions",
                columns: new[] { "ScopeId", "DataClassKey", "CompletedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM workspaces.staff_onboarding_retention_executions
                    ) THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces while Staff onboarding retention execution evidence exists.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                UPDATE workspaces.tenant_destroy_operations
                SET "Stage" = 20
                WHERE "Stage" = 21;
                """);

            migrationBuilder.DropTable(
                name: "staff_onboarding_retention_executions",
                schema: "workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 20 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
        }
    }
}
