using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestRetentionTimeZoneEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAndCollectAffectedScopesSql);
            migrationBuilder.Sql(UpgradePreflightAndInvalidationSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_guests_property_projection_versions",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_policy",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_contract",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalTimeZoneId",
                schema: "guests",
                table: "property_projection",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneCatalogVersion",
                schema: "guests",
                table: "property_projection",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeZoneEvidenceSource",
                schema: "guests",
                table: "property_projection",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "TimeZoneEvidenceSourceVersion",
                schema: "guests",
                table: "property_projection",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "TimeZoneStatus",
                schema: "guests",
                table: "property_projection",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneCatalogVersion",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE guests.property_projection
                SET
                    "CanonicalTimeZoneId" = NULL,
                    "TimeZoneStatus" = 0,
                    "TimeZoneCatalogVersion" = NULL,
                    "TimeZoneEvidenceSource" =
                        CASE
                            WHEN "TimeZoneId" IS NULL THEN 0
                            ELSE 1
                        END,
                    "TimeZoneEvidenceSourceVersion" =
                        CASE
                            WHEN "TimeZoneId" IS NULL THEN 0
                            ELSE "TopologySourceVersion"
                        END;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guests_property_projection_time_zone_evidence",
                schema: "guests",
                table: "property_projection",
                sql: "(\"TimeZoneEvidenceSource\" = 0 AND \"TimeZoneStatus\" = 0 AND \"CanonicalTimeZoneId\" IS NULL AND \"TimeZoneCatalogVersion\" IS NULL AND \"TimeZoneEvidenceSourceVersion\" = 0) OR (\"TimeZoneEvidenceSource\" = 1 AND \"TimeZoneStatus\" = 0 AND \"CanonicalTimeZoneId\" IS NULL AND \"TimeZoneCatalogVersion\" IS NULL) OR (\"TimeZoneEvidenceSource\" IN (2, 4) AND \"TimeZoneId\" IS NOT NULL AND \"TimeZoneEvidenceSourceVersion\" >= 1 AND ((\"TimeZoneStatus\" = 1 AND \"CanonicalTimeZoneId\" = \"TimeZoneId\" AND \"TimeZoneCatalogVersion\" IS NOT NULL) OR (\"TimeZoneStatus\" = 2 AND \"CanonicalTimeZoneId\" IS NOT NULL AND \"CanonicalTimeZoneId\" <> \"TimeZoneId\" AND \"TimeZoneCatalogVersion\" IS NOT NULL) OR (\"TimeZoneStatus\" IN (3, 4, 5) AND \"CanonicalTimeZoneId\" IS NULL AND \"TimeZoneCatalogVersion\" IS NULL))) OR (\"TimeZoneEvidenceSource\" = 3 AND \"TimeZoneStatus\" = 1 AND \"TimeZoneId\" IS NOT NULL AND \"CanonicalTimeZoneId\" = \"TimeZoneId\" AND \"TimeZoneCatalogVersion\" IS NOT NULL AND \"TimeZoneEvidenceSourceVersion\" >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guests_property_projection_time_zone_text",
                schema: "guests",
                table: "property_projection",
                sql: "\"TimeZoneEvidenceSource\" NOT IN (2, 3, 4) OR (char_length(\"TimeZoneId\") > 0 AND \"TimeZoneId\" = btrim(\"TimeZoneId\") AND \"TimeZoneId\" !~ '[[:cntrl:]]' AND (\"CanonicalTimeZoneId\" IS NULL OR (char_length(\"CanonicalTimeZoneId\") > 0 AND \"CanonicalTimeZoneId\" = btrim(\"CanonicalTimeZoneId\") AND \"CanonicalTimeZoneId\" !~ '[[:cntrl:]]')) AND (\"TimeZoneCatalogVersion\" IS NULL OR (char_length(\"TimeZoneCatalogVersion\") > 0 AND \"TimeZoneCatalogVersion\" = btrim(\"TimeZoneCatalogVersion\") AND \"TimeZoneCatalogVersion\" !~ '[[:cntrl:]]')))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guests_property_projection_versions",
                schema: "guests",
                table: "property_projection",
                sql: "\"TopologySourceVersion\" >= 0 AND \"PolicySourceVersion\" >= 0 AND \"TimeZoneEvidenceSourceVersion\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_policy",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND \"DeadlineUtc\" > \"StartedAtUtc\" AND (\"State\" <> 1 OR \"ExecutionPolicyVersion\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_contract",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"ContractVersion\" BETWEEN 1 AND 2 AND ((\"ContractVersion\" = 1 AND \"TimeZoneCatalogVersion\" IS NULL) OR (\"ContractVersion\" = 2 AND char_length(\"TimeZoneCatalogVersion\") > 0 AND \"TimeZoneCatalogVersion\" = btrim(\"TimeZoneCatalogVersion\") AND \"TimeZoneCatalogVersion\" !~ '[[:cntrl:]]'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAndCollectAffectedScopesSql);
            migrationBuilder.Sql(DowngradePreflightAndInvalidationSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_guests_property_projection_time_zone_evidence",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guests_property_projection_time_zone_text",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guests_property_projection_versions",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_executions_policy",
                schema: "guests",
                table: "guest_retention_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_receipts_contract",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.DropColumn(
                name: "CanonicalTimeZoneId",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropColumn(
                name: "TimeZoneCatalogVersion",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropColumn(
                name: "TimeZoneEvidenceSource",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropColumn(
                name: "TimeZoneEvidenceSourceVersion",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropColumn(
                name: "TimeZoneStatus",
                schema: "guests",
                table: "property_projection");

            migrationBuilder.DropColumn(
                name: "TimeZoneCatalogVersion",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guests_property_projection_versions",
                schema: "guests",
                table: "property_projection",
                sql: "\"TopologySourceVersion\" >= 0 AND \"PolicySourceVersion\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_executions_policy",
                schema: "guests",
                table: "guest_retention_executions",
                sql: "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND \"DeadlineUtc\" > \"StartedAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_receipts_contract",
                schema: "guests",
                table: "guest_retention_anonymisation_receipts",
                sql: "\"ContractVersion\" = 1");
        }

        private const string LockAndCollectAffectedScopesSql =
            """
            LOCK TABLE
                guests.tenant_revisions,
                guests.tenant_destroy_operations,
                guests.tenant_destroy_receipts,
                guests.data_hold_receipts,
                guests.data_holds,
                guests.guest_anonymisation_receipts,
                guests.guest_anonymisation_restore_receipts,
                guests.guest_anonymisation_tombstones,
                guests.guest_data_rights_correction_receipts,
                guests.guest_operation_locks,
                guests.guest_processing_restriction_receipts,
                guests.guest_processing_restriction_state,
                guests.guest_processing_restrictions,
                guests.guest_profiles,
                guests.guest_retention_anonymisation_receipts,
                guests.guest_retention_executions,
                guests.guest_retention_sweep_checkpoints,
                guests.inbox_messages,
                guests.management_operations,
                guests.outbox_messages,
                guests.projection_rebuild_checkpoints,
                guests.property_policy_acknowledgements,
                guests.property_projection,
                guests.stay_history
            IN ACCESS EXCLUSIVE MODE NOWAIT;

            CREATE TEMPORARY TABLE
                guests_retention_tz_affected_scopes
            (
                "ScopeId" character varying(128) PRIMARY KEY
            )
            ON COMMIT DROP;

            INSERT INTO guests_retention_tz_affected_scopes ("ScopeId")
            SELECT revision."ScopeId"
            FROM guests.tenant_revisions revision
            WHERE revision."LifecycleStatus" = 1
            UNION
            SELECT record."ScopeId" FROM guests.data_hold_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.data_holds record
            UNION
            SELECT record."ScopeId" FROM guests.guest_anonymisation_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.guest_anonymisation_restore_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.guest_anonymisation_tombstones record
            UNION
            SELECT record."ScopeId" FROM guests.guest_data_rights_correction_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.guest_operation_locks record
            UNION
            SELECT record."ScopeId" FROM guests.guest_processing_restriction_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.guest_processing_restriction_state record
            UNION
            SELECT record."ScopeId" FROM guests.guest_processing_restrictions record
            UNION
            SELECT record."ScopeId" FROM guests.guest_profiles record
            UNION
            SELECT record."ScopeId" FROM guests.guest_retention_anonymisation_receipts record
            UNION
            SELECT record."ScopeId" FROM guests.guest_retention_executions record
            UNION
            SELECT record."ScopeId" FROM guests.guest_retention_sweep_checkpoints record
            UNION
            SELECT record."ScopeId" FROM guests.inbox_messages record
            WHERE record."ScopeId" IS NOT NULL
            UNION
            SELECT record."ScopeId" FROM guests.management_operations record
            UNION
            SELECT record."ScopeId" FROM guests.outbox_messages record
            WHERE record."ScopeId" IS NOT NULL
            UNION
            SELECT record."ScopeId" FROM guests.projection_rebuild_checkpoints record
            UNION
            SELECT record."ScopeId" FROM guests.property_policy_acknowledgements record
            UNION
            SELECT record."ScopeId" FROM guests.property_projection record
            UNION
            SELECT record."ScopeId" FROM guests.stay_history record;
            """;

        private const string UpgradePreflightAndInvalidationSql =
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM guests.guest_retention_executions execution
                    WHERE execution."State" = 1)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot add Guests retention time-zone evidence while a retention execution is running.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests.tenant_revisions revision
                    WHERE revision."LifecycleStatus" = 2)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot change Guests retention time-zone evidence while tenant destruction is in progress.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests_retention_tz_affected_scopes affected
                    INNER JOIN guests.tenant_revisions revision
                        ON revision."ScopeId" = affected."ScopeId"
                    WHERE revision."LifecycleStatus" = 3)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot change Guests retention time-zone evidence because a completed tenant still owns exportable data.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests_retention_tz_affected_scopes affected
                    INNER JOIN guests.tenant_revisions revision
                        ON revision."ScopeId" = affected."ScopeId"
                    WHERE revision."Revision" = 9223372036854775807)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot invalidate Guests tenant revisions because an affected revision is exhausted.';
                END IF;
            END;
            $$;

            INSERT INTO guests.tenant_revisions AS revision
                ("ScopeId", "Revision", "LifecycleStatus")
            SELECT affected."ScopeId", 1, 1
            FROM guests_retention_tz_affected_scopes affected
            ON CONFLICT ("ScopeId") DO UPDATE
            SET "Revision" = revision."Revision" + 1;
            """;

        private const string DowngradePreflightAndInvalidationSql =
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM guests.guest_retention_anonymisation_receipts receipt
                    WHERE receipt."ContractVersion" >= 2
                       OR receipt."TimeZoneCatalogVersion" IS NOT NULL)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot remove Guests retention time-zone evidence while version 2 receipts exist.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests.property_projection property
                    WHERE property."CanonicalTimeZoneId" IS NOT NULL
                       OR property."TimeZoneCatalogVersion" IS NOT NULL
                       OR property."TimeZoneStatus" <> 0
                       OR property."TimeZoneEvidenceSource" NOT IN (0, 1)
                       OR (property."TimeZoneEvidenceSource" = 0 AND
                           property."TimeZoneEvidenceSourceVersion" <> 0))
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot remove Guests retention time-zone evidence while projected time-zone evidence exists.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests.guest_retention_executions execution
                    WHERE execution."ExecutionPolicyVersion" >= 2)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot remove Guests retention time-zone evidence while execution policy version 2 state exists.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests.tenant_revisions revision
                    WHERE revision."LifecycleStatus" = 2)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot change Guests retention time-zone evidence while tenant destruction is in progress.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests_retention_tz_affected_scopes affected
                    INNER JOIN guests.tenant_revisions revision
                        ON revision."ScopeId" = affected."ScopeId"
                    WHERE revision."LifecycleStatus" = 3)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot change Guests retention time-zone evidence because a completed tenant still owns exportable data.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM guests_retention_tz_affected_scopes affected
                    INNER JOIN guests.tenant_revisions revision
                        ON revision."ScopeId" = affected."ScopeId"
                    WHERE revision."Revision" = 9223372036854775807)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Cannot invalidate Guests tenant revisions because an affected revision is exhausted.';
                END IF;
            END;
            $$;

            INSERT INTO guests.tenant_revisions AS revision
                ("ScopeId", "Revision", "LifecycleStatus")
            SELECT affected."ScopeId", 1, 1
            FROM guests_retention_tz_affected_scopes affected
            ON CONFLICT ("ScopeId") DO UPDATE
            SET "Revision" = revision."Revision" + 1;
            """;
    }
}
