using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Retention.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionRunRetryRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_tenant_destroy_operation_progress",
                schema: "retention",
                table: "tenant_destroy_operations");

            migrationBuilder.Sql(
                """
                UPDATE retention.tenant_destroy_operations
                SET "Stage" = CASE
                    WHEN "Stage" = 1 THEN 2
                    ELSE "Stage" + 2
                END;
                """);

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "retention",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "run_retry_requests",
                schema: "retention",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DataClassKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    EvidenceVersion = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_retry_requests", x => x.Id);
                    table.CheckConstraint("CK_retention_run_retry_request_state", "\"State\" IN (1, 2, 3) AND ((\"State\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"FailureCode\" IS NULL) OR (\"State\" = 2 AND \"CompletedAtUtc\" IS NOT NULL AND \"FailureCode\" IS NULL) OR (\"State\" = 3 AND \"CompletedAtUtc\" IS NOT NULL AND \"FailureCode\" IS NOT NULL))");
                    table.CheckConstraint("CK_retention_run_retry_request_target", "(\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR (\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL)");
                    table.CheckConstraint("CK_retention_run_retry_request_timestamps", "(\"ScheduledAtUtc\" IS NULL OR \"ScheduledAtUtc\" >= \"RequestedAtUtc\") AND (\"CompletedAtUtc\" IS NULL OR \"CompletedAtUtc\" >= \"RequestedAtUtc\")");
                    table.CheckConstraint("CK_retention_run_retry_request_versions", "\"ExecutionPolicyVersion\" >= 1 AND \"EvidenceVersion\" >= 1 AND \"Attempt\" >= 1 AND \"Version\" >= 1");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_tenant_destroy_operation_progress",
                schema: "retention",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 8 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_state_ScopeId_LastExecutionId",
                schema: "retention",
                table: "schedule_state",
                columns: new[] { "ScopeId", "LastExecutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAtUtc_NextAttemptAtUtc_LockedUntil~",
                schema: "retention",
                table: "outbox_messages",
                columns: new[] { "ProcessedAtUtc", "NextAttemptAtUtc", "LockedUntilUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_run_retry_requests_ScopeId_RunId_EvidenceVersion",
                schema: "retention",
                table: "run_retry_requests",
                columns: new[] { "ScopeId", "RunId", "EvidenceVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_run_retry_requests_ScopeId_State_RequestedAtUtc",
                schema: "retention",
                table: "run_retry_requests",
                columns: new[] { "ScopeId", "State", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "retention");

            migrationBuilder.DropTable(
                name: "run_retry_requests",
                schema: "retention");

            migrationBuilder.DropCheckConstraint(
                name: "CK_retention_tenant_destroy_operation_progress",
                schema: "retention",
                table: "tenant_destroy_operations");

            migrationBuilder.Sql(
                """
                UPDATE retention.tenant_destroy_operations
                SET "Stage" = CASE
                    WHEN "Stage" <= 3 THEN 1
                    ELSE "Stage" - 2
                END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_schedule_state_ScopeId_LastExecutionId",
                schema: "retention",
                table: "schedule_state");

            migrationBuilder.AddCheckConstraint(
                name: "CK_retention_tenant_destroy_operation_progress",
                schema: "retention",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 6 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
        }
    }
}
