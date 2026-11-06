using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Staff.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffIdentityProvisioningAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "staff"."tenant_revisions" IN SHARE MODE NOWAIT;
                LOCK TABLE "staff"."member_mutation_operations" IN SHARE MODE NOWAIT;
                LOCK TABLE "staff"."staff_members" IN SHARE MODE NOWAIT;

                DO $preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "staff"."tenant_revisions" state
                        WHERE state."LifecycleStatus" = 2)
                    THEN
                        RAISE EXCEPTION
                            'Cannot install Staff identity provisioning anchors while a tenant destruction is in progress';
                    END IF;
                END;
                $preflight$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_tenant_destroy_operation_progress",
                schema: "staff",
                table: "tenant_destroy_operations");

            migrationBuilder.CreateTable(
                name: "identity_provisioning_anchors",
                schema: "staff",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnchoredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolutionEventId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_provisioning_anchors", x => new { x.ScopeId, x.SourceKind, x.SourceId });
                    table.CheckConstraint("CK_staff_identity_provisioning_anchors_ids", "\"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_staff_identity_provisioning_anchors_resolution_event", "(\"SourceKind\" = 1 AND \"ResolutionEventId\" IS NOT NULL AND \"ResolutionEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ResolutionEventId\" <> \"SourceId\") OR (\"SourceKind\" = 2 AND \"ResolutionEventId\" IS NULL)");
                    table.CheckConstraint("CK_staff_identity_provisioning_anchors_source_kind", "\"SourceKind\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_identity_provisioning_anchors_staff_members_ScopeId_StaffMe~",
                        columns: x => new { x.ScopeId, x.StaffMemberId },
                        principalSchema: "staff",
                        principalTable: "staff_members",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.UniqueConstraint(
                        "AK_identity_provisioning_anchors_ScopeId_SourceKind_SourceId_S~",
                        x => new { x.ScopeId, x.SourceKind, x.SourceId, x.StaffMemberId });
                });

            migrationBuilder.CreateTable(
                name: "identity_provisioning_anchor_resolutions",
                schema: "staff",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    ResolutionEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_provisioning_anchor_resolutions", x => new { x.ScopeId, x.SourceKind, x.SourceId });
                    table.CheckConstraint("CK_staff_identity_provisioning_anchor_resolutions_evidence", "\"WorkspaceApplicationVersion\" >= 1 AND \"Disposition\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_staff_identity_provisioning_anchor_resolutions_ids", "\"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ResolutionEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ResolutionEventId\" <> \"SourceId\"");
                    table.CheckConstraint("CK_staff_identity_provisioning_anchor_resolutions_source", "\"SourceKind\" = 1");
                    table.ForeignKey(
                        name: "FK_identity_provisioning_anchor_resolutions_identity_provision~",
                        columns: x => new { x.ScopeId, x.SourceKind, x.SourceId, x.StaffMemberId },
                        principalSchema: "staff",
                        principalTable: "identity_provisioning_anchors",
                        principalColumns: new[] { "ScopeId", "SourceKind", "SourceId", "StaffMemberId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_tenant_destroy_operation_progress",
                schema: "staff",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 25 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchors_ResolutionEventId",
                schema: "staff",
                table: "identity_provisioning_anchors",
                column: "ResolutionEventId",
                unique: true,
                filter: "\"ResolutionEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchors_ScopeId_StaffMemberId_SourceK~",
                schema: "staff",
                table: "identity_provisioning_anchors",
                columns: new[] { "ScopeId", "StaffMemberId", "SourceKind", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchors_ScopeId_SourceKind_SourceId_~",
                schema: "staff",
                table: "identity_provisioning_anchors",
                columns: new[] { "ScopeId", "SourceKind", "SourceId", "StaffMemberId", "ResolutionEventId" },
                unique: true,
                filter: "\"ResolutionEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchor_resolutions_ResolutionEventId",
                schema: "staff",
                table: "identity_provisioning_anchor_resolutions",
                column: "ResolutionEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchor_resolutions_ScopeId_SourceKind~",
                schema: "staff",
                table: "identity_provisioning_anchor_resolutions",
                columns: new[] { "ScopeId", "SourceKind", "SourceId", "StaffMemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_provisioning_anchor_resolutions_ScopeId_StaffMembe~",
                schema: "staff",
                table: "identity_provisioning_anchor_resolutions",
                columns: new[] { "ScopeId", "StaffMemberId", "SourceId" });

            migrationBuilder.Sql(
                """
                INSERT INTO "staff"."identity_provisioning_anchors"
                    ("ScopeId", "SourceKind", "SourceId",
                     "StaffMemberId", "AnchoredAtUtc",
                     "ResolutionEventId")
                SELECT operation."ScopeId",
                       1,
                       operation."Id",
                       operation."StaffMemberId",
                       operation."CompletedAtUtc",
                       gen_random_uuid()
                FROM "staff"."member_mutation_operations" operation
                LEFT JOIN "staff"."tenant_revisions" state
                    ON state."ScopeId" = operation."ScopeId"
                WHERE operation."Kind" = 8
                  AND COALESCE(state."LifecycleStatus", 1) = 1
                ORDER BY operation."ScopeId", operation."Id";

                CREATE FUNCTION
                    "staff".prevent_identity_provisioning_anchor_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    destroy_operation_id text;
                    destroy_stage text;
                BEGIN
                    destroy_operation_id := current_setting(
                        'bunkfy.staff_tenant_destroy_operation_id',
                        true);
                    destroy_stage := current_setting(
                        'bunkfy.staff_tenant_destroy_stage',
                        true);
                    IF TG_OP = 'DELETE' AND
                       destroy_operation_id IS NOT NULL AND
                       destroy_stage = '24' AND
                       EXISTS (
                           SELECT 1
                           FROM "staff".tenant_destroy_operations operation
                           INNER JOIN "staff".tenant_revisions state
                               ON state."ScopeId" = operation."ScopeId"
                           WHERE operation."OperationId"::text =
                                     destroy_operation_id
                             AND operation."ScopeId" = OLD."ScopeId"
                             AND operation."Stage" IN (17, 24)
                             AND state."LifecycleStatus" = 2
                             AND state."DestroyOperationId" =
                                     operation."OperationId"
                             AND state."DestroyRequestSha256" =
                                     operation."RequestSha256")
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION
                        'Staff identity provisioning anchors are immutable';
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_identity_provisioning_anchors_immutable"
                BEFORE UPDATE OR DELETE
                ON "staff"."identity_provisioning_anchors"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "staff".prevent_identity_provisioning_anchor_mutation();

                CREATE FUNCTION
                    "staff".prevent_identity_provisioning_anchor_resolution_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    destroy_operation_id text;
                    destroy_stage text;
                BEGIN
                    destroy_operation_id := current_setting(
                        'bunkfy.staff_tenant_destroy_operation_id',
                        true);
                    destroy_stage := current_setting(
                        'bunkfy.staff_tenant_destroy_stage',
                        true);
                    IF TG_OP = 'DELETE' AND
                       destroy_operation_id IS NOT NULL AND
                       destroy_stage = '25' AND
                       EXISTS (
                           SELECT 1
                           FROM "staff".tenant_destroy_operations operation
                           INNER JOIN "staff".tenant_revisions state
                               ON state."ScopeId" = operation."ScopeId"
                           WHERE operation."OperationId"::text =
                                     destroy_operation_id
                             AND operation."ScopeId" = OLD."ScopeId"
                             AND operation."Stage" IN (24, 25)
                             AND state."LifecycleStatus" = 2
                             AND state."DestroyOperationId" =
                                     operation."OperationId"
                             AND state."DestroyRequestSha256" =
                                     operation."RequestSha256")
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION
                        'Staff identity provisioning anchor resolutions are immutable';
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_identity_provisioning_anchor_resolutions_immutable"
                BEFORE UPDATE OR DELETE
                ON "staff"."identity_provisioning_anchor_resolutions"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "staff".prevent_identity_provisioning_anchor_resolution_mutation();

                CREATE FUNCTION
                    "staff".enforce_identity_provisioning_anchor_resolution_integrity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM "staff".identity_provisioning_anchors anchor
                        WHERE anchor."ScopeId" = NEW."ScopeId"
                          AND anchor."SourceKind" = NEW."SourceKind"
                          AND anchor."SourceId" = NEW."SourceId"
                          AND anchor."StaffMemberId" = NEW."StaffMemberId"
                          AND anchor."ResolutionEventId" =
                              NEW."ResolutionEventId"
                          AND NEW."ResolvedAtUtc" >= anchor."AnchoredAtUtc")
                    THEN
                        RAISE EXCEPTION
                            'Staff identity provisioning anchor resolution does not match its exact anchor coordinates';
                    END IF;

                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_identity_provisioning_anchor_resolution_integrity"
                AFTER INSERT
                ON "staff"."identity_provisioning_anchor_resolutions"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "staff".enforce_identity_provisioning_anchor_resolution_integrity();

                CREATE FUNCTION
                    "staff".enforce_workspace_onboarding_receipt_anchor()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP IN ('INSERT', 'UPDATE') AND NEW."Kind" = 8 AND
                       NOT EXISTS (
                           SELECT 1
                           FROM "staff".identity_provisioning_anchors anchor
                           WHERE anchor."ScopeId" = NEW."ScopeId"
                             AND anchor."SourceKind" = 1
                             AND anchor."SourceId" = NEW."Id"
                             AND anchor."StaffMemberId" = NEW."StaffMemberId")
                    THEN
                        RAISE EXCEPTION
                            'Workspace onboarding receipt requires an exact Staff identity provisioning anchor';
                    END IF;

                    IF (TG_OP = 'DELETE' OR
                        (TG_OP = 'UPDATE' AND
                         (NEW."Kind" <> 8 OR
                          NEW."ScopeId" <> OLD."ScopeId" OR
                          NEW."Id" <> OLD."Id" OR
                          NEW."StaffMemberId" <> OLD."StaffMemberId"))) AND
                       OLD."Kind" = 8 AND
                       EXISTS (
                           SELECT 1
                           FROM "staff".identity_provisioning_anchors anchor
                           WHERE anchor."ScopeId" = OLD."ScopeId"
                             AND anchor."SourceKind" = 1
                             AND anchor."SourceId" = OLD."Id"
                             AND anchor."StaffMemberId" = OLD."StaffMemberId"
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM "staff".identity_provisioning_anchor_resolutions resolution
                                 WHERE resolution."ScopeId" = anchor."ScopeId"
                                   AND resolution."SourceKind" = anchor."SourceKind"
                                   AND resolution."SourceId" = anchor."SourceId"
                                   AND resolution."StaffMemberId" = anchor."StaffMemberId"
                                   AND resolution."ResolutionEventId" =
                                       anchor."ResolutionEventId"))
                    THEN
                        RAISE EXCEPTION
                            'Unresolved Workspace onboarding receipt cannot be removed';
                    END IF;

                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    "TR_staff_workspace_onboarding_receipt_anchor"
                AFTER INSERT OR UPDATE OR DELETE
                ON "staff"."member_mutation_operations"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    "staff".enforce_workspace_onboarding_receipt_anchor();

                CREATE FUNCTION
                    "staff".prevent_unresolved_workspace_anchor_identity_lifecycle()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF ((NEW."AuthSubjectId" IS DISTINCT FROM OLD."AuthSubjectId" AND
                         NOT (OLD."Status" <> 4 AND
                              NEW."Status" = 4 AND
                              NEW."AuthSubjectId" IS NULL)) OR
                        (NEW."Status" = 4 AND
                         OLD."Status" <> 4 AND
                         NEW."AuthSubjectId" IS NOT NULL)) AND
                       EXISTS (
                           SELECT 1
                           FROM "staff".identity_provisioning_anchors anchor
                           WHERE anchor."ScopeId" = OLD."ScopeId"
                             AND anchor."SourceKind" = 1
                             AND anchor."StaffMemberId" = OLD."Id")
                    THEN
                        RAISE EXCEPTION
                            'Workspace onboarding anchor requires access closure before Staff Auth subject change';
                    END IF;

                    IF ((NEW."AuthSubjectId" IS DISTINCT FROM OLD."AuthSubjectId" AND
                         NEW."Status" = 4) OR
                        (NEW."Status" = 4 AND OLD."Status" <> 4) OR
                        (NEW."Status" = 1 AND OLD."Status" <> 1)) AND
                       EXISTS (
                           SELECT 1
                           FROM "staff".identity_provisioning_anchors anchor
                           WHERE anchor."ScopeId" = OLD."ScopeId"
                             AND anchor."SourceKind" = 1
                             AND anchor."StaffMemberId" = OLD."Id"
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM "staff".identity_provisioning_anchor_resolutions resolution
                                 WHERE resolution."ScopeId" = anchor."ScopeId"
                                   AND resolution."SourceKind" = anchor."SourceKind"
                                   AND resolution."SourceId" = anchor."SourceId"
                                   AND resolution."StaffMemberId" = anchor."StaffMemberId"
                                   AND resolution."ResolutionEventId" =
                                       anchor."ResolutionEventId"))
                    THEN
                        RAISE EXCEPTION
                            'Unresolved Workspace onboarding anchor blocks Staff identity lifecycle mutation';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER
                    "TR_staff_members_unresolved_workspace_anchor_identity_lifecycle"
                BEFORE UPDATE OF "AuthSubjectId", "Status"
                ON "staff"."staff_members"
                FOR EACH ROW
                EXECUTE FUNCTION
                    "staff".prevent_unresolved_workspace_anchor_identity_lifecycle();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "staff"."tenant_revisions" IN SHARE MODE;
                LOCK TABLE "staff"."tenant_destroy_operations" IN SHARE MODE;
                LOCK TABLE "staff"."member_mutation_operations"
                    IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE "staff"."identity_provisioning_anchor_resolutions"
                    IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE "staff"."identity_provisioning_anchors"
                    IN ACCESS EXCLUSIVE MODE;

                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "staff"."identity_provisioning_anchor_resolutions") OR
                       EXISTS (
                        SELECT 1
                        FROM "staff"."identity_provisioning_anchors")
                    THEN
                        RAISE EXCEPTION
                            'Cannot remove Staff identity provisioning anchors while durable anchors exist';
                    END IF;
                END;
                $guard$;

                DROP TRIGGER IF EXISTS
                    "TR_staff_members_unresolved_workspace_anchor_identity_lifecycle"
                ON "staff"."staff_members";
                DROP FUNCTION IF EXISTS
                    "staff".prevent_unresolved_workspace_anchor_identity_lifecycle();

                DROP TRIGGER IF EXISTS
                    "TR_staff_workspace_onboarding_receipt_anchor"
                ON "staff"."member_mutation_operations";
                DROP FUNCTION IF EXISTS
                    "staff".enforce_workspace_onboarding_receipt_anchor();

                DROP TRIGGER IF EXISTS
                    "TR_staff_identity_provisioning_anchor_resolution_integrity"
                ON "staff"."identity_provisioning_anchor_resolutions";
                DROP FUNCTION IF EXISTS
                    "staff".enforce_identity_provisioning_anchor_resolution_integrity();

                DROP TRIGGER IF EXISTS
                    "TR_staff_identity_provisioning_anchor_resolutions_immutable"
                ON "staff"."identity_provisioning_anchor_resolutions";
                DROP FUNCTION IF EXISTS
                    "staff".prevent_identity_provisioning_anchor_resolution_mutation();

                DROP TRIGGER IF EXISTS
                    "TR_staff_identity_provisioning_anchors_immutable"
                ON "staff"."identity_provisioning_anchors";
                DROP FUNCTION IF EXISTS
                    "staff".prevent_identity_provisioning_anchor_mutation();

                UPDATE "staff"."tenant_destroy_operations"
                SET "Stage" = 17
                WHERE "Stage" IN (24, 25);
                """);

            migrationBuilder.DropTable(
                name: "identity_provisioning_anchor_resolutions",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "identity_provisioning_anchors",
                schema: "staff");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_tenant_destroy_operation_progress",
                schema: "staff",
                table: "tenant_destroy_operations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_tenant_destroy_operation_progress",
                schema: "staff",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 23 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
        }
    }
}
