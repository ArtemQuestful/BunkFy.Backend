using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Retention.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionControlPlaneAuthoritativeStateIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_result",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_target",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_versions",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_run_retry_request_target",
                schema: "retention",
                table: "run_retry_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_run_retry_request_versions",
                schema: "retention",
                table: "run_retry_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_coordinate",
                schema: "retention",
                table: "executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_time",
                schema: "retention",
                table: "executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_versions",
                schema: "retention",
                table: "executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_coordinates",
                schema: "retention",
                table: "schedule_state",
                sql: "trim(\"ScopeId\") <> '' AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"OwnerKey\" ~ '^[a-z0-9.-]+$' AND \"DataClassKey\" ~ '^[a-z0-9.-]+$' AND (\"OutcomeCode\" IS NULL OR \"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_result",
                schema: "retention",
                table: "schedule_state",
                sql: "(\"State\" = 1 AND \"LastCompletedAtUtc\" IS NULL AND \"LastScannedCount\" IS NULL AND \"LastAffectedCount\" IS NULL AND \"LastRemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL AND \"HoldReviewDueAtUtc\" IS NULL) OR (\"State\" = 2 AND \"LastCompletedAtUtc\" IS NOT NULL AND \"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND \"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND \"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NULL AND \"ConsecutiveFailures\" = 0) OR (\"State\" = 3 AND \"LastCompletedAtUtc\" IS NOT NULL AND \"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND \"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND \"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NOT NULL AND \"ConsecutiveFailures\" = 0) OR (\"State\" = 4 AND \"LastCompletedAtUtc\" IS NOT NULL AND \"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND \"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND \"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NULL AND \"ConsecutiveFailures\" >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_target",
                schema: "retention",
                table: "schedule_state",
                sql: "(\"TargetKey\" = 'tenant' AND \"PropertyId\" IS NULL) OR (\"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"TargetKey\" = replace(\"PropertyId\"::text, '-', ''))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_versions",
                schema: "retention",
                table: "schedule_state",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Version\" >= 1 AND \"ConsecutiveFailures\" >= 0 AND (\"State\" = 1 OR \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_run_retry_request_coordinates",
                schema: "retention",
                table: "run_retry_requests",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"RunId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND \"OwnerKey\" ~ '^[a-z0-9.-]+$' AND \"DataClassKey\" ~ '^[a-z0-9.-]+$' AND (\"FailureCode\" IS NULL OR \"FailureCode\" ~ '^[a-z0-9.-]+$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_run_retry_request_target",
                schema: "retention",
                table: "run_retry_requests",
                sql: "(\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR (\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_run_retry_request_versions",
                schema: "retention",
                table: "run_retry_requests",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"EvidenceVersion\" >= 1 AND \"Attempt\" >= 1 AND \"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_coordinate",
                schema: "retention",
                table: "executions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND ((\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR (\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_keys",
                schema: "retention",
                table: "executions",
                sql: "\"OwnerKey\" ~ '^[a-z0-9.-]+$' AND \"DataClassKey\" ~ '^[a-z0-9.-]+$' AND (\"OutcomeCode\" IS NULL OR \"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_time",
                schema: "retention",
                table: "executions",
                sql: "\"DeadlineUtc\" > \"StartedAtUtc\" AND (\"CompletedAtUtc\" IS NULL OR \"CompletedAtUtc\" >= \"StartedAtUtc\") AND (\"State\" = 4 OR \"CompletedAtUtc\" IS NULL OR \"CompletedAtUtc\" <= \"DeadlineUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_versions",
                schema: "retention",
                table: "executions",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND \"Version\" >= 1 AND (\"State\" = 1 OR \"Version\" >= 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_coordinates",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_result",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_target",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_schedule_state_versions",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_run_retry_request_coordinates",
                schema: "retention",
                table: "run_retry_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_run_retry_request_target",
                schema: "retention",
                table: "run_retry_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_run_retry_request_versions",
                schema: "retention",
                table: "run_retry_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_coordinate",
                schema: "retention",
                table: "executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_keys",
                schema: "retention",
                table: "executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_time",
                schema: "retention",
                table: "executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_executions_versions",
                schema: "retention",
                table: "executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_result",
                schema: "retention",
                table: "schedule_state",
                sql: "\"State\" IN (1, 2, 3, 4) AND ((\"LastCompletedAtUtc\" IS NULL AND \"LastScannedCount\" IS NULL AND \"LastAffectedCount\" IS NULL AND \"LastRemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL) OR (\"LastCompletedAtUtc\" IS NOT NULL AND \"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND \"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND length(trim(\"OutcomeCode\")) > 0))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_target",
                schema: "retention",
                table: "schedule_state",
                sql: "(\"TargetKey\" = 'tenant' AND \"PropertyId\" IS NULL) OR (char_length(\"TargetKey\") = 32 AND \"PropertyId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_schedule_state_versions",
                schema: "retention",
                table: "schedule_state",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Version\" >= 1 AND \"ConsecutiveFailures\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_run_retry_request_target",
                schema: "retention",
                table: "run_retry_requests",
                sql: "(\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR (\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_run_retry_request_versions",
                schema: "retention",
                table: "run_retry_requests",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"EvidenceVersion\" >= 1 AND \"Attempt\" >= 1 AND \"Version\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_coordinate",
                schema: "retention",
                table: "executions",
                sql: "(\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR (\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_time",
                schema: "retention",
                table: "executions",
                sql: "\"DeadlineUtc\" > \"StartedAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_executions_versions",
                schema: "retention",
                table: "executions",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND \"Version\" >= 1");
        }
    }
}
