using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunkFy.Modules.Workspaces.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceStaffIdentityProvisioningAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET LOCAL lock_timeout = '1ms';

                LOCK TABLE "workspaces"."inbox_messages"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."outbox_messages"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_access_processes"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_access_profile_snapshots"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_correlation_anonymisation_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_correlation_anonymisation_restore_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_deferred_claim_withdrawals"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_applications"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_correction_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_processing_restriction_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_retention_correlation_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."tenant_destroy_operations"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."tenant_destroy_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."workspace_termination_fence_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."workspace_termination_fences"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;

                DO $lock_tasks$
                BEGIN
                    IF to_regclass('tasks.task_runs') IS NOT NULL THEN
                        EXECUTE
                            'LOCK TABLE "tasks"."task_runs" ' ||
                            'IN SHARE MODE NOWAIT';
                    END IF;
                END;
                $lock_tasks$;

                SET LOCAL lock_timeout = '0';

                DO $preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."tenant_destroy_operations"
                        WHERE "Stage" NOT BETWEEN 1 AND 21)
                    THEN
                        RAISE EXCEPTION
                            'Workspaces identity-anchor migration found an unknown tenant destruction stage';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_onboarding_applications"
                        WHERE "StaffMemberId" IS NOT NULL
                          AND ("VerifiedAccountEmail" IS NOT NULL OR
                               "DisplayName" IS NOT NULL OR
                               "LegalName" IS NOT NULL OR
                               "WorkEmail" IS NOT NULL OR
                               "WorkPhone" IS NOT NULL OR
                               "EmployeeNumber" IS NOT NULL OR
                               "JobTitle" IS NOT NULL OR
                               "Department" IS NOT NULL)
                          AND "Version" = 9223372036854775807)
                    THEN
                        RAISE EXCEPTION
                            'Workspaces identity-anchor migration cannot advance a legacy onboarding version past bigint';
                    END IF;
                END;
                $preflight$;

                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations");

            // Published stages 1..21 retain their meanings. New cleanup
            // stages are appended; Completed (21) is never rewritten.

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_pending_profile",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_staff",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_terminal_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAnchorContinuationEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAnchorExpectedResolutionEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IdentityAnchorResolutionApplicationVersion",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdentityAnchorResolutionDisposition",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAnchorResolutionEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IdentityAnchorResolutionIntentAtUtc",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IdentityAnchorResolutionObservedAtUtc",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAnchorResolutionStaffMemberId",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IdentityAnchorSweepOrdinal",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AddColumn<int?>(
                name: "RestorationDisposition",
                schema: "workspaces",
                table: "staff_access_processes",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "workspaces"."staff_onboarding_applications"
                SET "VerifiedAccountEmail" = NULL,
                    "DisplayName" = NULL,
                    "LegalName" = NULL,
                    "WorkEmail" = NULL,
                    "WorkPhone" = NULL,
                    "EmployeeNumber" = NULL,
                    "JobTitle" = NULL,
                    "Department" = NULL,
                    "Version" = "Version" + 1,
                    "LastChangedAtUtc" = GREATEST(
                        "LastChangedAtUtc",
                        statement_timestamp())
                WHERE "StaffMemberId" IS NOT NULL
                  AND ("VerifiedAccountEmail" IS NOT NULL OR
                       "DisplayName" IS NOT NULL OR
                       "LegalName" IS NOT NULL OR
                       "WorkEmail" IS NOT NULL OR
                       "WorkPhone" IS NOT NULL OR
                       "EmployeeNumber" IS NOT NULL OR
                       "JobTitle" IS NOT NULL OR
                       "Department" IS NOT NULL);

                UPDATE "workspaces"."staff_access_processes"
                SET "RestorationDisposition" = CASE
                    WHEN "TargetState" = 1 THEN 2
                    WHEN "TargetState" IN (2, 3) THEN 1
                    ELSE NULL
                END;

                DO $backfill$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_access_processes"
                        WHERE "RestorationDisposition" IS NULL)
                    THEN
                        RAISE EXCEPTION
                            'Workspaces access restoration disposition backfill found an unknown target state';
                    END IF;
                END;
                $backfill$;

                ALTER TABLE "workspaces"."staff_access_processes"
                    ALTER COLUMN "RestorationDisposition" SET NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "staff_historical_no_provision_receipts",
                schema: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedApplicationStatus = table.Column<int>(type: "integer", nullable: false),
                    ResultApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ResultApplicationStatus = table.Column<int>(type: "integer", nullable: false),
                    OrganizationsScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationsSourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationsSourceStatus = table.Column<int>(type: "integer", nullable: false),
                    StaffEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ExternalEvidenceManifestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ReviewerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_historical_no_provision_receipts", x => x.Id);
                    table.UniqueConstraint("AK_staff_historical_no_provision_receipts_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_ws_hist_no_prov_authority", "((\"SourceKind\" = 1 AND \"OrganizationsSourceStatus\" IN (3, 4, 5)) OR (\"SourceKind\" = 2 AND \"OrganizationsSourceStatus\" IN (7, 8, 9)))");
                    table.CheckConstraint("CK_ws_hist_no_prov_contract", "\"ContractVersion\" = 1");
                    table.CheckConstraint("CK_ws_hist_no_prov_hashes", "\"StaffEvidenceSha256\" ~ '^[0-9a-f]{64}$' AND \"ExternalEvidenceSha256\" ~ '^[0-9a-f]{64}$' AND \"CanonicalSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_ws_hist_no_prov_identifiers", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ApplicationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ExternalEvidenceManifestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_ws_hist_no_prov_reviewer", "char_length(\"ReviewerId\") BETWEEN 1 AND 256 AND \"ReviewerId\" = btrim(\"ReviewerId\") AND \"ReviewerId\" !~ '[[:cntrl:]]'");
                    table.CheckConstraint("CK_ws_hist_no_prov_transition", "((\"ExpectedApplicationStatus\" IN (5, 7, 8, 9, 10) AND \"ResultApplicationStatus\" = \"ExpectedApplicationStatus\") OR (\"ExpectedApplicationStatus\" IN (1, 2, 3, 4, 6) AND \"ResultApplicationStatus\" = 8 AND \"ResultApplicationVersion\" = \"ExpectedApplicationVersion\" + 1))");
                    table.CheckConstraint("CK_ws_hist_no_prov_versions", "\"ExpectedApplicationVersion\" >= 1 AND \"ResultApplicationVersion\" >= \"ExpectedApplicationVersion\" AND \"ResultApplicationVersion\" <= \"ExpectedApplicationVersion\" + 1 AND \"OrganizationsScopeRevision\" >= 0 AND \"OrganizationsSourceVersion\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "workspace_staff_identity_anchor_sweep_checkpoints",
                schema: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CycleUpperOrdinal = table.Column<long>(type: "bigint", nullable: true),
                    AfterOrdinal = table.Column<long>(type: "bigint", nullable: true),
                    CycleStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CycleScannedCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleNoAnchorCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleRemovedCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleAlreadyObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleDeferredCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleConflictCount = table.Column<long>(type: "bigint", nullable: false),
                    CyclePassOneCommittedCount = table.Column<long>(type: "bigint", nullable: false),
                    CycleResolutionRecordConfirmedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedCycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastCompletedUpperOrdinal = table.Column<long>(type: "bigint", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCompletedScannedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedNoAnchorCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedRemovedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedAlreadyObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedDeferredCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedConflictCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedPassOneCommittedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedResolutionRecordConfirmedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastAdvanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastAdvanceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_staff_identity_anchor_sweep_checkpoints", x => x.Id);
                    table.UniqueConstraint("AK_workspace_staff_identity_anchor_sweep_checkpoints_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_ws_anchor_sweep_cycle", "(\"CycleId\" IS NULL AND \"CycleUpperOrdinal\" IS NULL AND \"AfterOrdinal\" IS NULL AND \"CycleStartedAtUtc\" IS NULL AND \"CycleScannedCount\" = 0) OR (\"CycleId\" IS NOT NULL AND \"CycleUpperOrdinal\" > 0 AND (\"AfterOrdinal\" IS NULL OR (\"AfterOrdinal\" > 0 AND \"AfterOrdinal\" <= \"CycleUpperOrdinal\")) AND \"CycleStartedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_ws_anchor_sweep_cycle_counts_nonnegative", "\"CycleScannedCount\" >= 0 AND \"CycleNoAnchorCount\" >= 0 AND \"CycleRemovedCount\" >= 0 AND \"CycleObservedCount\" >= 0 AND \"CycleAlreadyObservedCount\" >= 0 AND \"CycleDeferredCount\" >= 0 AND \"CycleConflictCount\" >= 0 AND \"CyclePassOneCommittedCount\" >= 0 AND \"CycleResolutionRecordConfirmedCount\" >= 0");
                    table.CheckConstraint("CK_ws_anchor_sweep_cycle_counts_partition", "\"CycleScannedCount\" = \"CycleNoAnchorCount\" + \"CycleRemovedCount\" + \"CycleObservedCount\" + \"CycleAlreadyObservedCount\" + \"CycleDeferredCount\" + \"CycleConflictCount\" AND \"CyclePassOneCommittedCount\" <= \"CycleScannedCount\" AND \"CycleResolutionRecordConfirmedCount\" <= \"CycleScannedCount\"");
                    table.CheckConstraint("CK_ws_anchor_sweep_identifiers", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND (\"CycleId\" IS NULL OR \"CycleId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"LastCompletedCycleId\" IS NULL OR \"LastCompletedCycleId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"LastAdvanceId\" IS NULL OR \"LastAdvanceId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"LastRunId\" IS NULL OR \"LastRunId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("CK_ws_anchor_sweep_last_advance", "(\"LastAdvanceId\" IS NULL AND \"LastAdvanceSha256\" IS NULL) OR (\"LastAdvanceId\" IS NOT NULL AND \"LastAdvanceSha256\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_ws_anchor_sweep_last_counts_nonnegative", "\"LastCompletedScannedCount\" >= 0 AND \"LastCompletedNoAnchorCount\" >= 0 AND \"LastCompletedRemovedCount\" >= 0 AND \"LastCompletedObservedCount\" >= 0 AND \"LastCompletedAlreadyObservedCount\" >= 0 AND \"LastCompletedDeferredCount\" >= 0 AND \"LastCompletedConflictCount\" >= 0 AND \"LastCompletedPassOneCommittedCount\" >= 0 AND \"LastCompletedResolutionRecordConfirmedCount\" >= 0");
                    table.CheckConstraint("CK_ws_anchor_sweep_last_counts_partition", "\"LastCompletedScannedCount\" = \"LastCompletedNoAnchorCount\" + \"LastCompletedRemovedCount\" + \"LastCompletedObservedCount\" + \"LastCompletedAlreadyObservedCount\" + \"LastCompletedDeferredCount\" + \"LastCompletedConflictCount\" AND \"LastCompletedPassOneCommittedCount\" <= \"LastCompletedScannedCount\" AND \"LastCompletedResolutionRecordConfirmedCount\" <= \"LastCompletedScannedCount\"");
                    table.CheckConstraint("CK_ws_anchor_sweep_last_cycle", "(\"LastCompletedCycleId\" IS NULL AND \"LastCompletedUpperOrdinal\" IS NULL AND \"LastCompletedAtUtc\" IS NULL AND \"LastCompletedScannedCount\" = 0) OR (\"LastCompletedCycleId\" IS NOT NULL AND (\"LastCompletedUpperOrdinal\" > 0 OR (\"LastCompletedUpperOrdinal\" IS NULL AND \"LastCompletedScannedCount\" = 0)) AND \"LastCompletedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_ws_anchor_sweep_protocol", "\"ProtocolVersion\" = 1");
                    table.CheckConstraint("CK_ws_anchor_sweep_run", "(\"CycleId\" IS NULL AND \"LastCompletedCycleId\" IS NULL AND \"LastAdvanceId\" IS NULL AND \"LastRunId\" IS NULL) OR (\"LastRunId\" IS NOT NULL AND (\"CycleId\" IS NOT NULL OR \"LastCompletedCycleId\" IS NOT NULL))");
                    table.CheckConstraint("CK_ws_anchor_sweep_times", "(\"CycleStartedAtUtc\" IS NULL OR \"CycleStartedAtUtc\" <= \"UpdatedAtUtc\") AND (\"LastCompletedAtUtc\" IS NULL OR \"LastCompletedAtUtc\" <= \"UpdatedAtUtc\")");
                    table.CheckConstraint("CK_ws_anchor_sweep_version", "\"Version\" >= 1");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 23 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_applications_IdentityAnchorContinuationEve~",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                column: "IdentityAnchorContinuationEventId",
                unique: true,
                filter: "\"IdentityAnchorContinuationEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_applications_IdentityAnchorExpectedResolut~",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                column: "IdentityAnchorExpectedResolutionEventId",
                unique: true,
                filter: "\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_applications_ScopeId_IdentityAnchorSweepOr~",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                columns: new[] { "ScopeId", "IdentityAnchorSweepOrdinal" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_bound_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"StaffMemberId\" IS NULL OR (\"VerifiedAccountEmail\" IS NULL AND \"DisplayName\" IS NULL AND \"LegalName\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkPhone\" IS NULL AND \"EmployeeNumber\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_expected_resolution",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "(\"IdentityAnchorExpectedResolutionEventId\" IS NULL OR (\"StaffMemberId\" IS NOT NULL AND \"IdentityAnchorExpectedResolutionEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"IdentityAnchorExpectedResolutionEventId\" <> \"Id\")) AND (\"IdentityAnchorContinuationEventId\" IS NULL OR (\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL AND \"IdentityAnchorContinuationEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"IdentityAnchorContinuationEventId\" <> \"Id\" AND \"IdentityAnchorContinuationEventId\" <> \"IdentityAnchorExpectedResolutionEventId\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_coordinates",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"IdentityAnchorResolutionEventId\" IS NULL OR (\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL AND \"IdentityAnchorResolutionEventId\" = \"IdentityAnchorExpectedResolutionEventId\" AND \"StaffMemberId\" IS NOT NULL AND \"IdentityAnchorResolutionStaffMemberId\" = \"StaffMemberId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_intent",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "(\"IdentityAnchorResolutionEventId\" IS NULL AND \"IdentityAnchorResolutionStaffMemberId\" IS NULL AND \"IdentityAnchorResolutionApplicationVersion\" IS NULL AND \"IdentityAnchorResolutionDisposition\" IS NULL AND \"IdentityAnchorResolutionIntentAtUtc\" IS NULL) OR (\"IdentityAnchorResolutionEventId\" IS NOT NULL AND \"StaffMemberId\" IS NOT NULL AND \"IdentityAnchorResolutionStaffMemberId\" IS NOT NULL AND \"IdentityAnchorResolutionApplicationVersion\" > 0 AND \"IdentityAnchorResolutionApplicationVersion\" <= \"Version\" AND \"IdentityAnchorResolutionDisposition\" BETWEEN 1 AND 5 AND \"IdentityAnchorResolutionIntentAtUtc\" IS NOT NULL AND \"IdentityAnchorResolutionIntentAtUtc\" <= \"LastChangedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_observation",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"IdentityAnchorResolutionObservedAtUtc\" IS NULL OR (\"IdentityAnchorResolutionEventId\" IS NOT NULL AND \"IdentityAnchorResolutionObservedAtUtc\" >= \"IdentityAnchorResolutionIntentAtUtc\" AND \"IdentityAnchorResolutionObservedAtUtc\" <= \"LastChangedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_terminal",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"IdentityAnchorResolutionEventId\" IS NULL OR ((\"Status\" = 5 AND \"IdentityAnchorResolutionDisposition\" = 1) OR (\"Status\" = 7 AND \"IdentityAnchorResolutionDisposition\" = 2) OR (\"Status\" = 8 AND \"IdentityAnchorResolutionDisposition\" = 3) OR (\"Status\" = 9 AND \"IdentityAnchorResolutionDisposition\" = 4) OR (\"Status\" = 10 AND \"IdentityAnchorResolutionDisposition\" = 5))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_identity_anchor_sweep_ordinal",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"IdentityAnchorSweepOrdinal\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_pending_profile",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"StaffMemberId\" IS NOT NULL OR \"Status\" IN (5, 7, 8, 9, 10) OR (\"VerifiedAccountEmail\" IS NOT NULL AND \"DisplayName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_staff",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "(\"StaffMemberId\" IS NULL OR \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"Status\" NOT IN (4, 5) OR \"StaffMemberId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_terminal_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"Status\" NOT IN (4, 5, 7, 8, 9, 10) OR (\"VerifiedAccountEmail\" IS NULL AND \"DisplayName\" IS NULL AND \"LegalName\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkPhone\" IS NULL AND \"EmployeeNumber\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_access_process_restoration_disposition",
                schema: "workspaces",
                table: "staff_access_processes",
                sql: "(\"TargetState\" = 1 AND \"RestorationDisposition\" IN (2, 3)) OR (\"TargetState\" IN (2, 3) AND \"RestorationDisposition\" = 1)");

            migrationBuilder.CreateIndex(
                name: "IX_staff_historical_no_provision_receipts_ScopeId_ApplicationId",
                schema: "workspaces",
                table: "staff_historical_no_provision_receipts",
                columns: new[] { "ScopeId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_historical_no_provision_receipts_ScopeId_ExternalEvid~",
                schema: "workspaces",
                table: "staff_historical_no_provision_receipts",
                columns: new[] { "ScopeId", "ExternalEvidenceManifestId" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_historical_no_provision_receipts_ScopeId_OperationId",
                schema: "workspaces",
                table: "staff_historical_no_provision_receipts",
                columns: new[] { "ScopeId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_historical_no_provision_receipts_ScopeId_SourceKind_S~",
                schema: "workspaces",
                table: "staff_historical_no_provision_receipts",
                columns: new[] { "ScopeId", "SourceKind", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_staff_identity_anchor_sweep_checkpoints_ScopeId_L~",
                schema: "workspaces",
                table: "workspace_staff_identity_anchor_sweep_checkpoints",
                columns: new[] { "ScopeId", "LastCompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_staff_identity_anchor_sweep_checkpoints_ScopeId_P~",
                schema: "workspaces",
                table: "workspace_staff_identity_anchor_sweep_checkpoints",
                columns: new[] { "ScopeId", "ProtocolVersion" },
                unique: true);

            migrationBuilder.Sql(
                """
                -- The identity sequence is global. The INSERT trigger below
                -- replaces every candidate ordinal, including OVERRIDING SYSTEM
                -- VALUE input, with a fresh nextval. Global uniqueness remains a
                -- defense-in-depth invariant; burned identity values are harmless.
                CREATE UNIQUE INDEX
                    "UX_staff_onboarding_applications_IdentityAnchorSweepOrdinal"
                ON "workspaces"."staff_onboarding_applications"
                    ("IdentityAnchorSweepOrdinal");
                """);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION
                    "workspaces".tenant_destroy_delete_admitted(
                        requested_scope_id text,
                        requested_stage integer)
                RETURNS boolean
                LANGUAGE sql
                STABLE
                AS $function$
                    SELECT
                        requested_stage BETWEEN 1 AND 23
                        AND requested_stage <> 21
                        AND
                        current_setting(
                            'bunkfy.workspaces_tenant_destroy_operation_id',
                            true) IS NOT NULL
                        AND current_setting(
                            'bunkfy.workspaces_tenant_destroy_attempted_stage',
                            true) = requested_stage::text
                        AND EXISTS (
                            SELECT 1
                            FROM "workspaces"."tenant_destroy_operations" operation
                            INNER JOIN "workspaces"."workspace_termination_fences" fence
                                ON fence."ScopeId" = operation."ScopeId"
                               AND fence."Id" = operation."FenceId"
                            WHERE operation."OperationId"::text =
                                current_setting(
                                    'bunkfy.workspaces_tenant_destroy_operation_id',
                                    true)
                              AND operation."ScopeId" = requested_scope_id
                              AND operation."Stage" IN (
                                  requested_stage,
                                  CASE requested_stage
                                      WHEN 20 THEN 22
                                      WHEN 22 THEN 23
                                      WHEN 23 THEN 21
                                      ELSE requested_stage + 1
                                  END)
                              AND fence."State" = 2);
                $function$;

                CREATE OR REPLACE FUNCTION
                    "workspaces".prevent_receipt_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    required_stage integer;
                BEGIN
                    required_stage := CASE TG_TABLE_NAME
                        WHEN 'staff_onboarding_correction_receipts' THEN 5
                        WHEN 'staff_onboarding_recovery_receipts' THEN 5
                        WHEN 'staff_onboarding_processing_restriction_receipts' THEN 6
                        WHEN 'staff_retention_correlation_receipts' THEN 14
                        WHEN 'staff_correlation_anonymisation_restore_receipts' THEN 15
                        WHEN 'staff_correlation_anonymisation_receipts' THEN 17
                        WHEN 'workspace_termination_fence_receipts' THEN 18
                        WHEN 'staff_historical_no_provision_receipts' THEN 23
                        ELSE NULL
                    END;

                    IF TG_OP = 'DELETE'
                       AND required_stage IS NOT NULL
                       AND "workspaces".tenant_destroy_delete_admitted(
                           OLD."ScopeId",
                           required_stage)
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION 'workspace receipts are append-only';
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_onboarding_correction_receipts_append_only"
                BEFORE UPDATE OR DELETE
                ON "workspaces"."staff_onboarding_correction_receipts"
                FOR EACH ROW
                EXECUTE FUNCTION "workspaces".prevent_receipt_mutation();

                CREATE FUNCTION
                    "workspaces".enforce_staff_onboarding_sweep_ordinal()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    sequence_name text;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW."IdentityAnchorSweepOrdinal" IS DISTINCT FROM
                           OLD."IdentityAnchorSweepOrdinal"
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff onboarding sweep ordinals are immutable';
                        END IF;
                        RETURN NEW;
                    END IF;

                    sequence_name := pg_get_serial_sequence(
                        'workspaces.staff_onboarding_applications',
                        'IdentityAnchorSweepOrdinal');
                    IF sequence_name IS NULL THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding sweep ordinal sequence is unavailable';
                    END IF;

                    NEW."IdentityAnchorSweepOrdinal" :=
                        nextval(sequence_name::regclass);
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_onboarding_sweep_ordinal"
                BEFORE INSERT OR UPDATE OF "IdentityAnchorSweepOrdinal"
                ON "workspaces"."staff_onboarding_applications"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_staff_onboarding_sweep_ordinal();

                CREATE FUNCTION
                    "workspaces".enforce_staff_onboarding_anchor_coordinates()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    anchor_changed boolean;
                    resolution_was_empty boolean;
                    resolution_is_any boolean;
                    resolution_is_complete boolean;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StaffMemberId" IS NOT NULL
                           OR NEW."IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                           OR NEW."IdentityAnchorContinuationEventId" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionEventId" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionStaffMemberId" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionApplicationVersion" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionDisposition" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionIntentAtUtc" IS NOT NULL
                           OR NEW."IdentityAnchorResolutionObservedAtUtc" IS NOT NULL
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff onboarding applications must be inserted without Staff or identity-anchor coordinates';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD."StaffMemberId" IS NOT NULL
                       AND NEW."StaffMemberId" IS DISTINCT FROM OLD."StaffMemberId"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding anchor Staff coordinate is immutable';
                    END IF;

                    IF OLD."StaffMemberId" IS NULL
                       AND NEW."StaffMemberId" IS NOT NULL
                       AND NEW."IdentityAnchorExpectedResolutionEventId" IS NULL
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding Staff binding requires an expected resolution coordinate';
                    END IF;

                    IF OLD."IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                       AND NEW."IdentityAnchorExpectedResolutionEventId" IS DISTINCT FROM
                           OLD."IdentityAnchorExpectedResolutionEventId"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding expected resolution coordinate is immutable';
                    END IF;

                    IF OLD."IdentityAnchorContinuationEventId" IS NOT NULL
                       AND NEW."IdentityAnchorContinuationEventId" IS DISTINCT FROM
                           OLD."IdentityAnchorContinuationEventId"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding continuation coordinate is immutable';
                    END IF;

                    IF OLD."IdentityAnchorContinuationEventId" IS NULL
                       AND NEW."IdentityAnchorContinuationEventId" IS NOT NULL
                       AND OLD."IdentityAnchorResolutionEventId" IS NOT NULL
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding continuation cannot be appended after terminal resolution';
                    END IF;

                    IF OLD."IdentityAnchorExpectedResolutionEventId" IS NULL
                       AND NEW."IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                       AND ((NEW."IdentityAnchorContinuationEventId" IS NULL) =
                            (NEW."IdentityAnchorResolutionEventId" IS NULL))
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding initial anchor binding requires exactly one continuation or terminal resolution';
                    END IF;

                    resolution_was_empty :=
                        OLD."IdentityAnchorResolutionEventId" IS NULL
                        AND OLD."IdentityAnchorResolutionStaffMemberId" IS NULL
                        AND OLD."IdentityAnchorResolutionApplicationVersion" IS NULL
                        AND OLD."IdentityAnchorResolutionDisposition" IS NULL
                        AND OLD."IdentityAnchorResolutionIntentAtUtc" IS NULL;
                    resolution_is_any :=
                        NEW."IdentityAnchorResolutionEventId" IS NOT NULL
                        OR NEW."IdentityAnchorResolutionStaffMemberId" IS NOT NULL
                        OR NEW."IdentityAnchorResolutionApplicationVersion" IS NOT NULL
                        OR NEW."IdentityAnchorResolutionDisposition" IS NOT NULL
                        OR NEW."IdentityAnchorResolutionIntentAtUtc" IS NOT NULL;
                    resolution_is_complete :=
                        NEW."IdentityAnchorResolutionEventId" IS NOT NULL
                        AND NEW."IdentityAnchorResolutionStaffMemberId" IS NOT NULL
                        AND NEW."IdentityAnchorResolutionApplicationVersion" IS NOT NULL
                        AND NEW."IdentityAnchorResolutionDisposition" IS NOT NULL
                        AND NEW."IdentityAnchorResolutionIntentAtUtc" IS NOT NULL;

                    IF resolution_is_any <> resolution_is_complete THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding resolution coordinates must be recorded atomically';
                    END IF;

                    IF NOT resolution_was_empty
                       AND (NEW."IdentityAnchorResolutionEventId",
                            NEW."IdentityAnchorResolutionStaffMemberId",
                            NEW."IdentityAnchorResolutionApplicationVersion",
                            NEW."IdentityAnchorResolutionDisposition",
                            NEW."IdentityAnchorResolutionIntentAtUtc") IS DISTINCT FROM
                           (OLD."IdentityAnchorResolutionEventId",
                            OLD."IdentityAnchorResolutionStaffMemberId",
                            OLD."IdentityAnchorResolutionApplicationVersion",
                            OLD."IdentityAnchorResolutionDisposition",
                            OLD."IdentityAnchorResolutionIntentAtUtc")
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding resolution coordinates are immutable';
                    END IF;

                    IF NEW."IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                       AND NEW."IdentityAnchorContinuationEventId" IS NULL
                       AND NEW."IdentityAnchorResolutionEventId" IS NULL
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding expected resolution requires continuation or terminal resolution';
                    END IF;

                    IF resolution_was_empty AND resolution_is_complete
                       AND (NEW."IdentityAnchorResolutionEventId" IS DISTINCT FROM
                                NEW."IdentityAnchorExpectedResolutionEventId"
                            OR NEW."IdentityAnchorResolutionStaffMemberId" IS DISTINCT FROM
                                NEW."StaffMemberId"
                            OR NEW."IdentityAnchorResolutionApplicationVersion" IS DISTINCT FROM
                                NEW."Version"
                            OR NEW."IdentityAnchorResolutionIntentAtUtc" IS DISTINCT FROM
                                NEW."LastChangedAtUtc")
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding resolution does not match the exact terminal coordinates';
                    END IF;

                    IF NEW."IdentityAnchorContinuationEventId" IS NOT NULL
                       AND NEW."IdentityAnchorResolutionEventId" IS NULL
                       AND NOT (
                           (NEW."Status" = 4
                            AND NEW."FailureCode" IS NULL)
                           OR (NEW."Status" = 6
                               AND NULLIF(btrim(NEW."FailureCode"), '') IS NOT NULL
                               AND OLD."Status" IN (4, 6)
                               AND OLD."StaffMemberId" IS NOT NULL
                               AND OLD."IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                               AND OLD."IdentityAnchorContinuationEventId" IS NOT NULL
                               AND NEW."Version" > OLD."Version"))
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding continuation requires Staff-ready or a bound recoverable failure';
                    END IF;

                    IF resolution_is_complete
                       AND NEW."FailureCode" IS NOT NULL
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding terminal anchor resolution cannot retain a failure code';
                    END IF;

                    IF OLD."IdentityAnchorResolutionObservedAtUtc" IS NOT NULL
                       AND NEW."IdentityAnchorResolutionObservedAtUtc" IS DISTINCT FROM
                           OLD."IdentityAnchorResolutionObservedAtUtc"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding resolution observation is immutable';
                    END IF;

                    IF OLD."IdentityAnchorResolutionObservedAtUtc" IS NULL
                       AND NEW."IdentityAnchorResolutionObservedAtUtc" IS NOT NULL
                       AND (OLD."IdentityAnchorResolutionEventId" IS NULL
                            OR NEW."IdentityAnchorResolutionEventId" IS NULL
                            OR OLD.xmin::text::numeric = mod(
                                pg_current_xact_id()::text::numeric,
                                4294967296)
                            OR NEW."IdentityAnchorResolutionObservedAtUtc" IS DISTINCT FROM
                                NEW."LastChangedAtUtc")
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding resolution observation must match the exact observed transition';
                    END IF;

                    anchor_changed :=
                        NEW."StaffMemberId" IS DISTINCT FROM OLD."StaffMemberId"
                        OR NEW."IdentityAnchorExpectedResolutionEventId" IS DISTINCT FROM
                            OLD."IdentityAnchorExpectedResolutionEventId"
                        OR NEW."IdentityAnchorContinuationEventId" IS DISTINCT FROM
                            OLD."IdentityAnchorContinuationEventId"
                        OR resolution_is_any <> NOT resolution_was_empty
                        OR NEW."IdentityAnchorResolutionObservedAtUtc" IS DISTINCT FROM
                            OLD."IdentityAnchorResolutionObservedAtUtc";
                    -- Application Version counts domain transitions, not row
                    -- writes. Acceptance, membership binding and Staff-ready
                    -- may be persisted together by the same unit of work.
                    -- EF still compares the original persisted Version, and
                    -- terminal resolution must capture the exact new Version.
                    IF anchor_changed AND NEW."Version" <= OLD."Version" THEN
                        RAISE EXCEPTION
                            'Workspace Staff onboarding anchor transitions must advance the application version';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_onboarding_anchor_coordinates"
                BEFORE INSERT OR UPDATE
                ON "workspaces"."staff_onboarding_applications"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_staff_onboarding_anchor_coordinates();

                CREATE FUNCTION
                    "workspaces".reject_staff_onboarding_delete_without_destroy()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF "workspaces".tenant_destroy_delete_admitted(
                        OLD."ScopeId",
                        13)
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION
                        'Workspace Staff onboarding applications may only be deleted by admitted tenant destruction stage 13';
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_onboarding_delete_guard"
                BEFORE DELETE
                ON "workspaces"."staff_onboarding_applications"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".reject_staff_onboarding_delete_without_destroy();

                CREATE FUNCTION
                    "workspaces".reject_access_restoration_disposition_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."RestorationDisposition" IS DISTINCT FROM
                       OLD."RestorationDisposition"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff access restoration disposition is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_access_process_restoration_disposition_immutable"
                BEFORE UPDATE OF "RestorationDisposition"
                ON "workspaces"."staff_access_processes"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".reject_access_restoration_disposition_mutation();

                CREATE FUNCTION
                    "workspaces".enforce_suppressed_access_process_has_no_snapshots()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."RestorationDisposition" = 3
                       AND EXISTS (
                           SELECT 1
                           FROM "workspaces"."staff_access_profile_snapshots" snapshot
                           WHERE snapshot."ProcessId" = NEW."Id")
                    THEN
                        RAISE EXCEPTION
                            'Suppressed Workspace Staff access restoration cannot retain profile snapshots';
                    END IF;
                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_access_process_suppressed_snapshots"
                AFTER INSERT OR UPDATE
                ON "workspaces"."staff_access_processes"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_suppressed_access_process_has_no_snapshots();

                CREATE FUNCTION
                    "workspaces".enforce_access_snapshot_parent_not_suppressed()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_access_processes" process
                        WHERE process."Id" = NEW."ProcessId"
                          AND process."RestorationDisposition" = 3)
                    THEN
                        RAISE EXCEPTION
                            'Suppressed Workspace Staff access restoration cannot accept profile snapshots';
                    END IF;
                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_access_snapshot_parent_not_suppressed"
                AFTER INSERT OR UPDATE
                ON "workspaces"."staff_access_profile_snapshots"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_access_snapshot_parent_not_suppressed();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION
                    "workspaces".is_exactly_reviewed_staff_onboarding(
                        application "workspaces"."staff_onboarding_applications")
                RETURNS boolean
                LANGUAGE sql
                STABLE
                STRICT
                AS $function$
                    SELECT EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_historical_no_provision_receipts" receipt
                        WHERE receipt."ScopeId" = application."ScopeId"
                          AND receipt."ContractVersion" = 1
                          AND receipt."ApplicationId" = application."Id"
                          AND receipt."SourceKind" = application."SourceKind"
                          AND receipt."SourceId" = application."SourceId"
                          AND receipt."ResultApplicationVersion" = application."Version"
                          AND receipt."ResultApplicationStatus" = application."Status"
                          AND receipt."ResultApplicationStatus" IN (5, 7, 8, 9, 10)
                          AND application."SubjectId" =
                              'no-provision:' || receipt."Id"::text
                          AND application."VerifiedAccountEmail" IS NULL
                          AND application."DisplayName" IS NULL
                          AND application."LegalName" IS NULL
                          AND application."WorkEmail" IS NULL
                          AND application."WorkPhone" IS NULL
                          AND application."EmployeeNumber" IS NULL
                          AND application."JobTitle" IS NULL
                          AND application."Department" IS NULL
                          AND application."FailureCode" IS NULL
                          AND application."StaffMemberId" IS NULL
                          AND application."IdentityAnchorExpectedResolutionEventId" IS NULL
                          AND application."IdentityAnchorContinuationEventId" IS NULL
                          AND application."IdentityAnchorResolutionEventId" IS NULL
                          AND application."IdentityAnchorResolutionStaffMemberId" IS NULL
                          AND application."IdentityAnchorResolutionApplicationVersion" IS NULL
                          AND application."IdentityAnchorResolutionDisposition" IS NULL
                          AND application."IdentityAnchorResolutionIntentAtUtc" IS NULL
                          AND application."IdentityAnchorResolutionObservedAtUtc" IS NULL);
                $function$;

                CREATE FUNCTION
                    "workspaces".workspace_anchor_sweep_empty_sha256(
                        cycle_id uuid,
                        run_id uuid)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                STRICT
                AS $function$
                    SELECT encode(
                        sha256(
                            convert_to(
                                'empty|' || cycle_id::text || '|' ||
                                run_id::text,
                                'UTF8')),
                        'hex');
                $function$;

                CREATE FUNCTION
                    "workspaces".workspace_anchor_sweep_advance_sha256(
                        expected_version bigint,
                        cycle_id uuid,
                        expected_after_ordinal bigint,
                        next_after_ordinal bigint,
                        reached_end boolean,
                        run_id uuid,
                        scanned_count bigint,
                        no_anchor_count bigint,
                        removed_count bigint,
                        observed_count bigint,
                        already_observed_count bigint,
                        deferred_count bigint,
                        conflict_count bigint,
                        pass_one_committed_count bigint,
                        resolution_record_confirmed_count bigint)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                AS $function$
                    SELECT encode(
                        sha256(
                            convert_to(
                                expected_version::text || '|' ||
                                cycle_id::text || '|' ||
                                COALESCE(
                                    expected_after_ordinal::text,
                                    'null') || '|' ||
                                next_after_ordinal::text || '|' ||
                                CASE WHEN reached_end THEN '1' ELSE '0' END ||
                                '|' || run_id::text || '|' ||
                                scanned_count::text || '|' ||
                                no_anchor_count::text || '|' ||
                                removed_count::text || '|' ||
                                observed_count::text || '|' ||
                                already_observed_count::text || '|' ||
                                deferred_count::text || '|' ||
                                conflict_count::text || '|' ||
                                pass_one_committed_count::text || '|' ||
                                resolution_record_confirmed_count::text,
                                'UTF8')),
                        'hex');
                $function$;

                CREATE FUNCTION
                    "workspaces".enforce_identity_anchor_sweep_checkpoint_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    last_completed_changed boolean;
                    all_cycle_counts_zero boolean;
                    all_last_counts_zero boolean;
                    expected_empty_digest text;
                    expected_advance_digest text;
                    eligible_count bigint;
                    last_eligible_ordinal bigint;
                    delta_scanned bigint;
                    delta_no_anchor bigint;
                    delta_removed bigint;
                    delta_observed bigint;
                    delta_already_observed bigint;
                    delta_deferred bigint;
                    delta_conflict bigint;
                    delta_pass_one bigint;
                    delta_resolution_confirmed bigint;
                BEGIN
                    all_cycle_counts_zero :=
                        NEW."CycleScannedCount" = 0
                        AND NEW."CycleNoAnchorCount" = 0
                        AND NEW."CycleRemovedCount" = 0
                        AND NEW."CycleObservedCount" = 0
                        AND NEW."CycleAlreadyObservedCount" = 0
                        AND NEW."CycleDeferredCount" = 0
                        AND NEW."CycleConflictCount" = 0
                        AND NEW."CyclePassOneCommittedCount" = 0
                        AND NEW."CycleResolutionRecordConfirmedCount" = 0;
                    all_last_counts_zero :=
                        NEW."LastCompletedScannedCount" = 0
                        AND NEW."LastCompletedNoAnchorCount" = 0
                        AND NEW."LastCompletedRemovedCount" = 0
                        AND NEW."LastCompletedObservedCount" = 0
                        AND NEW."LastCompletedAlreadyObservedCount" = 0
                        AND NEW."LastCompletedDeferredCount" = 0
                        AND NEW."LastCompletedConflictCount" = 0
                        AND NEW."LastCompletedPassOneCommittedCount" = 0
                        AND NEW."LastCompletedResolutionRecordConfirmedCount" = 0;

                    IF TG_OP = 'INSERT' THEN
                        IF NEW."ProtocolVersion" <> 1
                           OR NEW."Id" =
                              '00000000-0000-0000-0000-000000000000'::uuid
                           OR btrim(NEW."ScopeId") = ''
                           OR NEW."UpdatedAtUtc" =
                              '0001-01-01 00:00:00+00'::timestamptz
                           OR NOT all_cycle_counts_zero
                           OR NOT all_last_counts_zero
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep checkpoint initial state is invalid';
                        END IF;

                        IF NEW."Version" = 1
                           AND NEW."CycleId" IS NULL
                           AND NEW."CycleUpperOrdinal" IS NULL
                           AND NEW."AfterOrdinal" IS NULL
                           AND NEW."CycleStartedAtUtc" IS NULL
                           AND NEW."LastCompletedCycleId" IS NULL
                           AND NEW."LastCompletedUpperOrdinal" IS NULL
                           AND NEW."LastCompletedAtUtc" IS NULL
                           AND NEW."LastAdvanceId" IS NULL
                           AND NEW."LastAdvanceSha256" IS NULL
                           AND NEW."LastRunId" IS NULL
                        THEN
                            RETURN NEW;
                        END IF;

                        IF NEW."Version" = 2
                           AND NEW."CycleId" IS NOT NULL
                           AND NEW."CycleUpperOrdinal" > 0
                           AND NEW."CycleUpperOrdinal" IS NOT DISTINCT FROM (
                               SELECT max(application."IdentityAnchorSweepOrdinal")
                               FROM "workspaces"."staff_onboarding_applications" application
                               WHERE application."ScopeId" = NEW."ScopeId")
                           AND NEW."AfterOrdinal" IS NULL
                           AND NEW."CycleStartedAtUtc" IS NOT DISTINCT FROM
                              NEW."UpdatedAtUtc"
                           AND NEW."LastCompletedCycleId" IS NULL
                           AND NEW."LastCompletedUpperOrdinal" IS NULL
                           AND NEW."LastCompletedAtUtc" IS NULL
                           AND NEW."LastAdvanceId" IS NULL
                           AND NEW."LastAdvanceSha256" IS NULL
                           AND NEW."LastRunId" IS NOT NULL
                        THEN
                            RETURN NEW;
                        END IF;

                        IF NEW."Version" = 2
                           AND NEW."CycleId" IS NULL
                           AND NEW."CycleUpperOrdinal" IS NULL
                           AND NEW."AfterOrdinal" IS NULL
                           AND NEW."CycleStartedAtUtc" IS NULL
                           AND NEW."LastCompletedCycleId" IS NOT NULL
                           AND NEW."LastCompletedUpperOrdinal" IS NULL
                           AND NEW."LastCompletedAtUtc" IS NOT DISTINCT FROM
                              NEW."UpdatedAtUtc"
                           AND NEW."LastAdvanceId" IS NOT NULL
                           AND NEW."LastAdvanceSha256" IS NOT NULL
                           AND NEW."LastRunId" IS NOT NULL
                           AND NOT EXISTS (
                               SELECT 1
                               FROM "workspaces"."staff_onboarding_applications" application
                               WHERE application."ScopeId" = NEW."ScopeId")
                        THEN
                            expected_empty_digest := "workspaces".
                                workspace_anchor_sweep_empty_sha256(
                                    NEW."LastCompletedCycleId",
                                    NEW."LastRunId");
                            IF NEW."LastAdvanceSha256" = expected_empty_digest THEN
                                RETURN NEW;
                            END IF;
                        END IF;

                        RAISE EXCEPTION
                            'Workspace Staff identity-anchor sweep checkpoint initial state is invalid';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF "workspaces".tenant_destroy_delete_admitted(
                            OLD."ScopeId",
                            22)
                        THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION
                            'Workspace Staff identity-anchor sweep checkpoints may only be deleted by admitted tenant destruction stage 22';
                    END IF;

                    IF (NEW."Id", NEW."ScopeId", NEW."ProtocolVersion") IS DISTINCT FROM
                       (OLD."Id", OLD."ScopeId", OLD."ProtocolVersion")
                       OR NEW."Version" <> OLD."Version" + 1
                       OR NEW."UpdatedAtUtc" < OLD."UpdatedAtUtc"
                    THEN
                        RAISE EXCEPTION
                            'Workspace Staff identity-anchor sweep checkpoint identity or version is invalid';
                    END IF;

                    last_completed_changed :=
                        (NEW."LastCompletedCycleId",
                         NEW."LastCompletedUpperOrdinal",
                         NEW."LastCompletedAtUtc",
                         NEW."LastCompletedScannedCount",
                         NEW."LastCompletedNoAnchorCount",
                         NEW."LastCompletedRemovedCount",
                         NEW."LastCompletedObservedCount",
                         NEW."LastCompletedAlreadyObservedCount",
                         NEW."LastCompletedDeferredCount",
                         NEW."LastCompletedConflictCount",
                         NEW."LastCompletedPassOneCommittedCount",
                         NEW."LastCompletedResolutionRecordConfirmedCount") IS DISTINCT FROM
                        (OLD."LastCompletedCycleId",
                         OLD."LastCompletedUpperOrdinal",
                         OLD."LastCompletedAtUtc",
                         OLD."LastCompletedScannedCount",
                         OLD."LastCompletedNoAnchorCount",
                         OLD."LastCompletedRemovedCount",
                         OLD."LastCompletedObservedCount",
                         OLD."LastCompletedAlreadyObservedCount",
                         OLD."LastCompletedDeferredCount",
                         OLD."LastCompletedConflictCount",
                         OLD."LastCompletedPassOneCommittedCount",
                         OLD."LastCompletedResolutionRecordConfirmedCount");

                    IF OLD."CycleId" IS NULL AND NEW."CycleId" IS NOT NULL THEN
                        IF NEW."AfterOrdinal" IS NOT NULL
                           OR NEW."CycleStartedAtUtc" IS DISTINCT FROM
                              NEW."UpdatedAtUtc"
                           OR NEW."CycleUpperOrdinal" IS DISTINCT FROM (
                               SELECT max(application."IdentityAnchorSweepOrdinal")
                               FROM "workspaces"."staff_onboarding_applications" application
                               WHERE application."ScopeId" = NEW."ScopeId")
                           OR NEW."CycleScannedCount" <> 0
                           OR last_completed_changed
                           OR NEW."LastAdvanceId" IS DISTINCT FROM OLD."LastAdvanceId"
                           OR NEW."LastAdvanceSha256" IS DISTINCT FROM OLD."LastAdvanceSha256"
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep begin transition is invalid';
                        END IF;
                    ELSIF OLD."CycleId" IS NULL AND NEW."CycleId" IS NULL THEN
                        IF NOT last_completed_changed
                           OR NEW."LastCompletedUpperOrdinal" IS NOT NULL
                           OR NEW."LastCompletedAtUtc" IS DISTINCT FROM
                              NEW."UpdatedAtUtc"
                           OR NEW."LastCompletedScannedCount" <> 0
                           OR NEW."LastAdvanceId" IS NOT DISTINCT FROM OLD."LastAdvanceId"
                           OR EXISTS (
                               SELECT 1
                               FROM "workspaces"."staff_onboarding_applications" application
                               WHERE application."ScopeId" = NEW."ScopeId")
                           OR NEW."LastAdvanceSha256" IS DISTINCT FROM
                              "workspaces".workspace_anchor_sweep_empty_sha256(
                                  NEW."LastCompletedCycleId",
                                  NEW."LastRunId")
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor empty completion transition is invalid';
                        END IF;
                    ELSIF OLD."CycleId" IS NOT NULL AND NEW."CycleId" IS NOT NULL THEN
                        IF (NEW."CycleId", NEW."CycleUpperOrdinal", NEW."CycleStartedAtUtc")
                               IS DISTINCT FROM
                           (OLD."CycleId", OLD."CycleUpperOrdinal", OLD."CycleStartedAtUtc")
                           OR NEW."AfterOrdinal" IS NULL
                           OR (OLD."AfterOrdinal" IS NOT NULL AND
                               NEW."AfterOrdinal" <= OLD."AfterOrdinal")
                           OR NEW."CycleScannedCount" < OLD."CycleScannedCount"
                           OR NEW."CycleNoAnchorCount" < OLD."CycleNoAnchorCount"
                           OR NEW."CycleRemovedCount" < OLD."CycleRemovedCount"
                           OR NEW."CycleObservedCount" < OLD."CycleObservedCount"
                           OR NEW."CycleAlreadyObservedCount" < OLD."CycleAlreadyObservedCount"
                           OR NEW."CycleDeferredCount" < OLD."CycleDeferredCount"
                           OR NEW."CycleConflictCount" < OLD."CycleConflictCount"
                           OR NEW."CyclePassOneCommittedCount" < OLD."CyclePassOneCommittedCount"
                           OR NEW."CycleResolutionRecordConfirmedCount" <
                              OLD."CycleResolutionRecordConfirmedCount"
                           OR last_completed_changed
                           OR NEW."LastAdvanceId" IS NOT DISTINCT FROM OLD."LastAdvanceId"
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep advance transition is invalid';
                        END IF;

                        delta_scanned := NEW."CycleScannedCount" -
                            OLD."CycleScannedCount";
                        delta_no_anchor := NEW."CycleNoAnchorCount" -
                            OLD."CycleNoAnchorCount";
                        delta_removed := NEW."CycleRemovedCount" -
                            OLD."CycleRemovedCount";
                        delta_observed := NEW."CycleObservedCount" -
                            OLD."CycleObservedCount";
                        delta_already_observed :=
                            NEW."CycleAlreadyObservedCount" -
                            OLD."CycleAlreadyObservedCount";
                        delta_deferred := NEW."CycleDeferredCount" -
                            OLD."CycleDeferredCount";
                        delta_conflict := NEW."CycleConflictCount" -
                            OLD."CycleConflictCount";
                        delta_pass_one := NEW."CyclePassOneCommittedCount" -
                            OLD."CyclePassOneCommittedCount";
                        delta_resolution_confirmed :=
                            NEW."CycleResolutionRecordConfirmedCount" -
                            OLD."CycleResolutionRecordConfirmedCount";

                        SELECT count(*)
                        INTO eligible_count
                        FROM "workspaces"."staff_onboarding_applications" application
                        WHERE application."ScopeId" = NEW."ScopeId"
                          AND application."IdentityAnchorSweepOrdinal" >
                              COALESCE(OLD."AfterOrdinal", 0)
                          AND application."IdentityAnchorSweepOrdinal" <=
                              NEW."AfterOrdinal"
                          AND NOT "workspaces".
                              is_exactly_reviewed_staff_onboarding(application);

                        expected_advance_digest := "workspaces".
                            workspace_anchor_sweep_advance_sha256(
                                OLD."Version",
                                OLD."CycleId",
                                OLD."AfterOrdinal",
                                NEW."AfterOrdinal",
                                false,
                                NEW."LastRunId",
                                delta_scanned,
                                delta_no_anchor,
                                delta_removed,
                                delta_observed,
                                delta_already_observed,
                                delta_deferred,
                                delta_conflict,
                                delta_pass_one,
                                delta_resolution_confirmed);
                        IF NEW."AfterOrdinal" >= OLD."CycleUpperOrdinal"
                           OR delta_scanned <= 0
                           OR delta_scanned <> eligible_count
                           OR delta_pass_one > delta_scanned
                           OR delta_resolution_confirmed > delta_scanned
                           OR NEW."LastAdvanceSha256" IS DISTINCT FROM
                              expected_advance_digest
                           OR NOT EXISTS (
                               SELECT 1
                               FROM "workspaces"."staff_onboarding_applications" application
                               WHERE application."ScopeId" = NEW."ScopeId"
                                 AND application."IdentityAnchorSweepOrdinal" >
                                     NEW."AfterOrdinal"
                                 AND application."IdentityAnchorSweepOrdinal" <=
                                     OLD."CycleUpperOrdinal"
                                 AND NOT "workspaces".
                                     is_exactly_reviewed_staff_onboarding(application))
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep advance proof is invalid';
                        END IF;
                    ELSIF OLD."CycleId" IS NOT NULL AND NEW."CycleId" IS NULL THEN
                        IF NEW."LastCompletedCycleId" IS DISTINCT FROM OLD."CycleId"
                           OR NEW."LastCompletedUpperOrdinal" IS DISTINCT FROM
                              OLD."CycleUpperOrdinal"
                           OR NEW."LastCompletedAtUtc" IS DISTINCT FROM NEW."UpdatedAtUtc"
                           OR NEW."LastCompletedScannedCount" < OLD."CycleScannedCount"
                           OR NEW."LastCompletedNoAnchorCount" < OLD."CycleNoAnchorCount"
                           OR NEW."LastCompletedRemovedCount" < OLD."CycleRemovedCount"
                           OR NEW."LastCompletedObservedCount" < OLD."CycleObservedCount"
                           OR NEW."LastCompletedAlreadyObservedCount" <
                              OLD."CycleAlreadyObservedCount"
                           OR NEW."LastCompletedDeferredCount" < OLD."CycleDeferredCount"
                           OR NEW."LastCompletedConflictCount" < OLD."CycleConflictCount"
                           OR NEW."LastCompletedPassOneCommittedCount" <
                              OLD."CyclePassOneCommittedCount"
                           OR NEW."LastCompletedResolutionRecordConfirmedCount" <
                              OLD."CycleResolutionRecordConfirmedCount"
                           OR NEW."CycleScannedCount" <> 0
                           OR NEW."LastAdvanceId" IS NOT DISTINCT FROM OLD."LastAdvanceId"
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep completion transition is invalid';
                        END IF;

                        delta_scanned := NEW."LastCompletedScannedCount" -
                            OLD."CycleScannedCount";
                        delta_no_anchor := NEW."LastCompletedNoAnchorCount" -
                            OLD."CycleNoAnchorCount";
                        delta_removed := NEW."LastCompletedRemovedCount" -
                            OLD."CycleRemovedCount";
                        delta_observed := NEW."LastCompletedObservedCount" -
                            OLD."CycleObservedCount";
                        delta_already_observed :=
                            NEW."LastCompletedAlreadyObservedCount" -
                            OLD."CycleAlreadyObservedCount";
                        delta_deferred := NEW."LastCompletedDeferredCount" -
                            OLD."CycleDeferredCount";
                        delta_conflict := NEW."LastCompletedConflictCount" -
                            OLD."CycleConflictCount";
                        delta_pass_one :=
                            NEW."LastCompletedPassOneCommittedCount" -
                            OLD."CyclePassOneCommittedCount";
                        delta_resolution_confirmed :=
                            NEW."LastCompletedResolutionRecordConfirmedCount" -
                            OLD."CycleResolutionRecordConfirmedCount";

                        SELECT count(*),
                               max(application."IdentityAnchorSweepOrdinal")
                        INTO eligible_count, last_eligible_ordinal
                        FROM "workspaces"."staff_onboarding_applications" application
                        WHERE application."ScopeId" = NEW."ScopeId"
                          AND application."IdentityAnchorSweepOrdinal" >
                              COALESCE(OLD."AfterOrdinal", 0)
                          AND application."IdentityAnchorSweepOrdinal" <=
                              OLD."CycleUpperOrdinal"
                          AND NOT "workspaces".
                              is_exactly_reviewed_staff_onboarding(application);
                        last_eligible_ordinal := COALESCE(
                            last_eligible_ordinal,
                            OLD."CycleUpperOrdinal");

                        expected_advance_digest := "workspaces".
                            workspace_anchor_sweep_advance_sha256(
                                OLD."Version",
                                OLD."CycleId",
                                OLD."AfterOrdinal",
                                last_eligible_ordinal,
                                true,
                                NEW."LastRunId",
                                delta_scanned,
                                delta_no_anchor,
                                delta_removed,
                                delta_observed,
                                delta_already_observed,
                                delta_deferred,
                                delta_conflict,
                                delta_pass_one,
                                delta_resolution_confirmed);
                        IF delta_scanned <> eligible_count
                           OR delta_pass_one > delta_scanned
                           OR delta_resolution_confirmed > delta_scanned
                           OR NEW."LastAdvanceSha256" IS DISTINCT FROM
                              expected_advance_digest
                        THEN
                            RAISE EXCEPTION
                                'Workspace Staff identity-anchor sweep completion proof is invalid';
                        END IF;
                    ELSE
                        RAISE EXCEPTION
                            'Workspace Staff identity-anchor sweep checkpoint transition is invalid';
                    END IF;

                    IF NEW."LastRunId" IS NULL THEN
                        RAISE EXCEPTION
                            'Workspace Staff identity-anchor sweep checkpoint transition requires a run coordinate';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER
                    "TR_workspace_staff_identity_anchor_sweep_checkpoint_mutation"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "workspaces"."workspace_staff_identity_anchor_sweep_checkpoints"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_identity_anchor_sweep_checkpoint_mutation();

                CREATE FUNCTION "workspaces".dotnet_string_length(value text)
                RETURNS integer
                LANGUAGE sql
                IMMUTABLE
                STRICT
                AS $function$
                    SELECT COALESCE(
                        sum(CASE WHEN ascii(ch) > 65535 THEN 2 ELSE 1 END),
                        0)::integer
                    FROM regexp_split_to_table(value, '') AS parts(ch);
                $function$;

                CREATE FUNCTION "workspaces".dotnet_length_prefix(value text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                STRICT
                AS $function$
                    SELECT "workspaces".dotnet_string_length(value)::text ||
                           ':' || value;
                $function$;

                CREATE FUNCTION
                    "workspaces".historical_no_provision_canonical_sha256(
                        receipt "workspaces"."staff_historical_no_provision_receipts")
                RETURNS text
                LANGUAGE plpgsql
                STABLE
                STRICT
                AS $function$
                DECLARE
                    canonical text;
                    reviewed_at text;
                BEGIN
                    reviewed_at := to_char(
                        receipt."ReviewedAtUtc" AT TIME ZONE 'UTC',
                        'YYYY-MM-DD"T"HH24:MI:SS.US') || '0+00:00';
                    canonical :=
                        "workspaces".dotnet_length_prefix(
                            'workspaces-staff-historical-no-provision|v1') ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ContractVersion"::text) ||
                        "workspaces".dotnet_length_prefix(
                            replace(receipt."Id"::text, '-', '')) ||
                        "workspaces".dotnet_length_prefix(receipt."ScopeId") ||
                        "workspaces".dotnet_length_prefix(
                            replace(receipt."OperationId"::text, '-', '')) ||
                        "workspaces".dotnet_length_prefix(
                            replace(receipt."ApplicationId"::text, '-', '')) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."SourceKind"::text) ||
                        "workspaces".dotnet_length_prefix(
                            replace(receipt."SourceId"::text, '-', '')) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ExpectedApplicationVersion"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ExpectedApplicationStatus"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ResultApplicationVersion"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ResultApplicationStatus"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."OrganizationsScopeRevision"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."OrganizationsSourceVersion"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."OrganizationsSourceStatus"::text) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."StaffEvidenceSha256") ||
                        "workspaces".dotnet_length_prefix(
                            replace(
                                receipt."ExternalEvidenceManifestId"::text,
                                '-',
                                '')) ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ExternalEvidenceSha256") ||
                        "workspaces".dotnet_length_prefix(
                            receipt."ReviewerId") ||
                        "workspaces".dotnet_length_prefix(reviewed_at);
                    RETURN encode(
                        sha256(convert_to(canonical, 'UTF8')),
                        'hex');
                END;
                $function$;

                CREATE FUNCTION
                    "workspaces".historical_no_provision_application_matches(
                        receipt "workspaces"."staff_historical_no_provision_receipts")
                RETURNS boolean
                LANGUAGE sql
                STABLE
                STRICT
                AS $function$
                    SELECT EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_onboarding_applications" application
                        WHERE application."ScopeId" = receipt."ScopeId"
                          AND application."Id" = receipt."ApplicationId"
                          AND application."SourceKind" = receipt."SourceKind"
                          AND application."SourceId" = receipt."SourceId"
                          AND application."Version" =
                              receipt."ResultApplicationVersion"
                          AND application."Status" =
                              receipt."ResultApplicationStatus"
                          AND receipt."ReviewedAtUtc" >=
                              application."LastChangedAtUtc"
                          AND application."SubjectId" =
                              'no-provision:' || receipt."Id"::text
                          AND application."VerifiedAccountEmail" IS NULL
                          AND application."DisplayName" IS NULL
                          AND application."LegalName" IS NULL
                          AND application."WorkEmail" IS NULL
                          AND application."WorkPhone" IS NULL
                          AND application."EmployeeNumber" IS NULL
                          AND application."JobTitle" IS NULL
                          AND application."Department" IS NULL
                          AND application."FailureCode" IS NULL
                          AND application."StaffMemberId" IS NULL
                          AND application."IdentityAnchorExpectedResolutionEventId" IS NULL
                          AND application."IdentityAnchorContinuationEventId" IS NULL
                          AND application."IdentityAnchorResolutionEventId" IS NULL
                          AND application."IdentityAnchorResolutionStaffMemberId" IS NULL
                          AND application."IdentityAnchorResolutionApplicationVersion" IS NULL
                          AND application."IdentityAnchorResolutionDisposition" IS NULL
                          AND application."IdentityAnchorResolutionIntentAtUtc" IS NULL
                          AND application."IdentityAnchorResolutionObservedAtUtc" IS NULL);
                $function$;

                CREATE FUNCTION
                    "workspaces".validate_historical_no_provision_receipt()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    PERFORM 1
                    FROM "workspaces"."staff_onboarding_applications" application
                    WHERE application."ScopeId" = NEW."ScopeId"
                      AND application."Id" = NEW."ApplicationId"
                    FOR UPDATE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION
                            'Workspace historical no-provision receipt application is unavailable';
                    END IF;

                    IF "workspaces".dotnet_string_length(NEW."ReviewerId")
                           NOT BETWEEN 1 AND 256
                       OR NEW."CanonicalSha256" IS DISTINCT FROM
                           "workspaces".historical_no_provision_canonical_sha256(NEW)
                       OR NOT "workspaces".historical_no_provision_application_matches(NEW)
                    THEN
                        RAISE EXCEPTION
                            'Workspace historical no-provision receipt does not match its canonical proof and exact application result';
                    END IF;
                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_historical_no_provision_receipt_integrity"
                AFTER INSERT
                ON "workspaces"."staff_historical_no_provision_receipts"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".validate_historical_no_provision_receipt();

                CREATE TRIGGER
                    "TR_staff_historical_no_provision_receipts_append_only"
                BEFORE UPDATE OR DELETE
                ON "workspaces"."staff_historical_no_provision_receipts"
                FOR EACH ROW
                EXECUTE FUNCTION "workspaces".prevent_receipt_mutation();

                CREATE FUNCTION
                    "workspaces".enforce_historical_no_provision_application_result()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_historical_no_provision_receipts" receipt
                        WHERE receipt."ScopeId" = NEW."ScopeId"
                          AND receipt."ApplicationId" = NEW."Id"
                          AND NOT "workspaces".
                              historical_no_provision_application_matches(receipt))
                    THEN
                        RAISE EXCEPTION
                            'Workspace historical no-provision application result cannot diverge or be resurrected';
                    END IF;
                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_onboarding_historical_no_provision_result"
                AFTER INSERT OR UPDATE
                ON "workspaces"."staff_onboarding_applications"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "workspaces".enforce_historical_no_provision_application_result();

                DO $validate$
                DECLARE
                    sequence_name text;
                BEGIN
                    sequence_name := pg_get_serial_sequence(
                        'workspaces.staff_onboarding_applications',
                        'IdentityAnchorSweepOrdinal');
                    IF sequence_name IS NULL
                       OR EXISTS (
                           SELECT 1
                           FROM "workspaces"."staff_onboarding_applications"
                           WHERE "IdentityAnchorSweepOrdinal" <= 0)
                    THEN
                        RAISE EXCEPTION
                            'Workspaces identity-anchor sweep ordinal installation is invalid';
                    END IF;
                END;
                $validate$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET LOCAL lock_timeout = '1ms';

                LOCK TABLE "workspaces"."inbox_messages"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."outbox_messages"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_access_processes"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_access_profile_snapshots"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_correlation_anonymisation_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_correlation_anonymisation_restore_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_deferred_claim_withdrawals"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_historical_no_provision_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_applications"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_correction_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_onboarding_processing_restriction_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."staff_retention_correlation_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."tenant_destroy_operations"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."tenant_destroy_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."workspace_staff_identity_anchor_sweep_checkpoints"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."workspace_termination_fence_receipts"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "workspaces"."workspace_termination_fences"
                    IN ACCESS EXCLUSIVE MODE NOWAIT;

                DO $guard$
                DECLARE
                    active_tasks boolean := false;
                BEGIN
                    IF to_regclass('tasks.task_runs') IS NOT NULL THEN
                        EXECUTE
                            'LOCK TABLE "tasks"."task_runs" ' ||
                            'IN SHARE MODE NOWAIT';
                    END IF;

                    PERFORM set_config('lock_timeout', '0', true);

                    IF to_regclass('tasks.task_runs') IS NOT NULL THEN
                        EXECUTE
                            'SELECT EXISTS (' ||
                            'SELECT 1 FROM "tasks"."task_runs" ' ||
                            'WHERE "ModuleName" = ''workspaces'' ' ||
                            'AND "TaskName" = ' ||
                            '''reconcile-staff-identity-anchors'' ' ||
                            'AND "Status" IN (1, 2, 3, 4, 5, 8))'
                            INTO active_tasks;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_historical_no_provision_receipts")
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while historical no-provision receipts exist';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."workspace_staff_identity_anchor_sweep_checkpoints")
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while sweep checkpoints exist';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."tenant_destroy_operations"
                        WHERE "Stage" IN (22, 23))
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while tenant destruction is in a new active stage';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_onboarding_applications"
                        WHERE "IdentityAnchorExpectedResolutionEventId" IS NOT NULL
                           OR "IdentityAnchorContinuationEventId" IS NOT NULL
                           OR "IdentityAnchorResolutionEventId" IS NOT NULL
                           OR "IdentityAnchorResolutionStaffMemberId" IS NOT NULL
                           OR "IdentityAnchorResolutionApplicationVersion" IS NOT NULL
                           OR "IdentityAnchorResolutionDisposition" IS NOT NULL
                           OR "IdentityAnchorResolutionIntentAtUtc" IS NOT NULL
                           OR "IdentityAnchorResolutionObservedAtUtc" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while onboarding anchor coordinates exist';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_access_processes"
                        WHERE "RestorationDisposition" = 3)
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while suppressed access restorations exist';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."staff_onboarding_applications"
                        WHERE "StaffMemberId" IS NOT NULL
                          AND "Status" IN (1, 2, 3, 4, 6)
                          AND "VerifiedAccountEmail" IS NULL
                          AND "DisplayName" IS NULL
                          AND "LegalName" IS NULL
                          AND "WorkEmail" IS NULL
                          AND "WorkPhone" IS NULL
                          AND "EmployeeNumber" IS NULL
                          AND "JobTitle" IS NULL
                          AND "Department" IS NULL)
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors after irreversible bound nonterminal applicant redaction';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "workspaces"."outbox_messages"
                        WHERE "ProcessedAtUtc" IS NULL
                          AND "EventType" IN (
                              'BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent',
                              'BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent',
                              'BunkFy.Modules.Staff.Contracts.StaffIdentityProvisioningAnchorCreatedIntegrationEvent',
                              'workspace-staff-onboarding-identity-anchor-continuation-requested',
                              'workspace-staff-onboarding-identity-anchor-resolved',
                              'workspace-onboarding-identity-anchor-created'))
                       OR EXISTS (
                        SELECT 1
                        FROM "workspaces"."inbox_messages"
                        WHERE "Status" IN (1, 2, 4)
                          AND "EventType" IN (
                              'BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent',
                              'BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent',
                              'BunkFy.Modules.Staff.Contracts.StaffIdentityProvisioningAnchorCreatedIntegrationEvent',
                              'workspace-staff-onboarding-identity-anchor-continuation-requested',
                              'workspace-staff-onboarding-identity-anchor-resolved',
                              'workspace-onboarding-identity-anchor-created'))
                    THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while continuation or resolution messages are pending';
                    END IF;

                    IF active_tasks THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Workspaces identity anchors while reconciliation tasks are active';
                    END IF;
                END;
                $guard$;

                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_historical_no_provision_result"
                    ON "workspaces"."staff_onboarding_applications";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_historical_no_provision_application_result();

                DROP TRIGGER IF EXISTS
                    "TR_staff_historical_no_provision_receipt_integrity"
                    ON "workspaces"."staff_historical_no_provision_receipts";
                DROP FUNCTION IF EXISTS
                    "workspaces".validate_historical_no_provision_receipt();

                DROP TRIGGER IF EXISTS
                    "TR_staff_historical_no_provision_receipts_append_only"
                    ON "workspaces"."staff_historical_no_provision_receipts";

                DROP FUNCTION IF EXISTS
                    "workspaces".historical_no_provision_application_matches(
                        "workspaces"."staff_historical_no_provision_receipts");
                DROP FUNCTION IF EXISTS
                    "workspaces".historical_no_provision_canonical_sha256(
                        "workspaces"."staff_historical_no_provision_receipts");
                DROP FUNCTION IF EXISTS
                    "workspaces".dotnet_length_prefix(text);
                DROP FUNCTION IF EXISTS
                    "workspaces".dotnet_string_length(text);

                DROP TRIGGER IF EXISTS
                    "TR_workspace_staff_identity_anchor_sweep_checkpoint_mutation"
                    ON "workspaces"."workspace_staff_identity_anchor_sweep_checkpoints";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_identity_anchor_sweep_checkpoint_mutation();
                DROP FUNCTION IF EXISTS
                    "workspaces".workspace_anchor_sweep_advance_sha256(
                        bigint, uuid, bigint, bigint, boolean, uuid,
                        bigint, bigint, bigint, bigint, bigint, bigint,
                        bigint, bigint, bigint);
                DROP FUNCTION IF EXISTS
                    "workspaces".workspace_anchor_sweep_empty_sha256(uuid, uuid);
                DROP FUNCTION IF EXISTS
                    "workspaces".is_exactly_reviewed_staff_onboarding(
                        "workspaces"."staff_onboarding_applications");

                DROP TRIGGER IF EXISTS
                    "TR_staff_access_snapshot_parent_not_suppressed"
                    ON "workspaces"."staff_access_profile_snapshots";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_access_snapshot_parent_not_suppressed();
                DROP TRIGGER IF EXISTS
                    "TR_staff_access_process_suppressed_snapshots"
                    ON "workspaces"."staff_access_processes";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_suppressed_access_process_has_no_snapshots();
                DROP TRIGGER IF EXISTS
                    "TR_staff_access_process_restoration_disposition_immutable"
                    ON "workspaces"."staff_access_processes";
                DROP FUNCTION IF EXISTS
                    "workspaces".reject_access_restoration_disposition_mutation();

                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_delete_guard"
                    ON "workspaces"."staff_onboarding_applications";
                DROP FUNCTION IF EXISTS
                    "workspaces".reject_staff_onboarding_delete_without_destroy();
                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_anchor_coordinates"
                    ON "workspaces"."staff_onboarding_applications";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_staff_onboarding_anchor_coordinates();
                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_sweep_ordinal"
                    ON "workspaces"."staff_onboarding_applications";
                DROP FUNCTION IF EXISTS
                    "workspaces".enforce_staff_onboarding_sweep_ordinal();

                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_correction_receipts_append_only"
                    ON "workspaces"."staff_onboarding_correction_receipts";

                CREATE OR REPLACE FUNCTION
                    "workspaces".prevent_receipt_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    destroy_operation_id text;
                BEGIN
                    destroy_operation_id := current_setting(
                        'bunkfy.workspaces_tenant_destroy_operation_id',
                        true);
                    IF TG_OP = 'DELETE' AND
                       destroy_operation_id IS NOT NULL AND
                       EXISTS (
                           SELECT 1
                           FROM "workspaces"."tenant_destroy_operations" operation
                           INNER JOIN
                               "workspaces"."workspace_termination_fences" fence
                               ON fence."ScopeId" = operation."ScopeId"
                              AND fence."Id" = operation."FenceId"
                           WHERE operation."OperationId"::text =
                                     destroy_operation_id
                             AND operation."ScopeId" = OLD."ScopeId"
                             AND fence."State" = 2)
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION 'workspace receipts are append-only';
                END;
                $function$;

                DROP FUNCTION IF EXISTS
                    "workspaces".tenant_destroy_delete_admitted(text, integer);

                """);

            migrationBuilder.DropTable(
                name: "staff_historical_no_provision_receipts",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "workspace_staff_identity_anchor_sweep_checkpoints",
                schema: "workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations");

            migrationBuilder.DropIndex(
                name: "IX_staff_onboarding_applications_IdentityAnchorContinuationEve~",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropIndex(
                name: "IX_staff_onboarding_applications_IdentityAnchorExpectedResolut~",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropIndex(
                name: "IX_staff_onboarding_applications_ScopeId_IdentityAnchorSweepOr~",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_bound_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_expected_resolution",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_coordinates",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_intent",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_observation",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_anchor_resolution_terminal",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_identity_anchor_sweep_ordinal",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_pending_profile",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_staff",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_terminal_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_access_process_restoration_disposition",
                schema: "workspaces",
                table: "staff_access_processes");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorContinuationEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorExpectedResolutionEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionApplicationVersion",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionDisposition",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionEventId",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionIntentAtUtc",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionObservedAtUtc",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorResolutionStaffMemberId",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "IdentityAnchorSweepOrdinal",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "RestorationDisposition",
                schema: "workspaces",
                table: "staff_access_processes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workspaces_tenant_destroy_operation_progress",
                schema: "workspaces",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 21 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_pending_profile",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"Status\" IN (5, 7, 8, 9, 10) OR (\"VerifiedAccountEmail\" IS NOT NULL AND \"DisplayName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_staff",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"Status\" NOT IN (4, 5) OR \"StaffMemberId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_terminal_redaction",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "\"Status\" NOT IN (5, 7, 8, 9, 10) OR (\"VerifiedAccountEmail\" IS NULL AND \"DisplayName\" IS NULL AND \"LegalName\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkPhone\" IS NULL AND \"EmployeeNumber\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL)");
        }
    }
}
