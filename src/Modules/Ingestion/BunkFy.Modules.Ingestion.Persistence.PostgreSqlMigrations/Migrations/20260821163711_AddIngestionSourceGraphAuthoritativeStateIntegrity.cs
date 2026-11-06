using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Ingestion.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionSourceGraphAuthoritativeStateIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_source_links_anonymised_shape",
                schema: "ingestion",
                table: "reservation_source_links",
                sql: "\"AnonymisedAtUtc\" IS NULL OR (\"State\" = 5 AND \"SourceReference\" = 'anonymised:' || replace(lower(CAST(\"Id\" AS text)), '-', '') AND \"ReservationId\" IS NULL AND \"LastObservedReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"LastObservedContentHash\") = '0000000000000000000000000000000000000000000000000000000000000000' AND \"LastObservedSourceRevision\" IS NULL AND \"LastAppliedSourceRevision\" IS NULL AND \"LastAppliedOperationalBaseline\" IS NULL AND \"ActiveProductOperationId\" IS NULL AND \"DeferredReceiptId\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" = \"AnonymisedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_source_links_applied_shape",
                schema: "ingestion",
                table: "reservation_source_links",
                sql: "((\"LastAppliedReceiptId\" IS NULL AND \"LastAppliedSourceRevision\" IS NULL AND \"LastAppliedSourceSequence\" IS NULL AND \"LastAppliedReservationDetailsRevision\" IS NULL AND \"LastAppliedOperationalBaseline\" IS NULL AND \"LastProductOperationId\" IS NULL) OR (\"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND \"LastProductOperationId\" IS NOT NULL AND \"LastProductOperationId\" <> '00000000-0000-0000-0000-000000000000' AND (\"LastAppliedSourceRevision\" IS NULL OR trim(\"LastAppliedSourceRevision\") <> '') AND (\"LastAppliedSourceSequence\" IS NULL OR \"LastAppliedSourceSequence\" >= 0) AND \"LastAppliedReservationDetailsRevision\" IS NOT NULL AND \"LastAppliedReservationDetailsRevision\" > 0 AND (\"LastAppliedOperationalBaseline\" IS NULL OR trim(\"LastAppliedOperationalBaseline\") <> '')))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_source_links_coordinates",
                schema: "ingestion",
                table: "reservation_source_links",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND trim(\"SourceSystem\") <> '' AND trim(\"SourceReference\") <> '' AND (\"ReservationId\" IS NULL OR \"ReservationId\" <> '00000000-0000-0000-0000-000000000000') AND (\"ActiveProductOperationId\" IS NULL OR \"ActiveProductOperationId\" <> '00000000-0000-0000-0000-000000000000') AND (\"DeferredReceiptId\" IS NULL OR \"DeferredReceiptId\" <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_source_links_lifecycle",
                schema: "ingestion",
                table: "reservation_source_links",
                sql: "\"State\" IN (1, 2, 3, 4, 5) AND \"Version\" >= 1 AND ((\"Version\" = 1 AND \"UpdatedAtUtc\" IS NULL) OR (\"Version\" >= 2 AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\")) AND ((\"State\" = 1 AND \"ReservationId\" IS NULL AND \"LastAppliedReceiptId\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 2 AND \"ReservationId\" IS NOT NULL AND \"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedOperationalBaseline\" IS NOT NULL AND trim(\"LastAppliedOperationalBaseline\") <> '' AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 3 AND \"ReservationId\" IS NOT NULL AND \"ActiveProductOperationId\" IS NOT NULL AND \"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedOperationalBaseline\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 4 AND \"ReservationId\" IS NOT NULL AND \"ActiveProductOperationId\" IS NULL AND \"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedOperationalBaseline\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 5 AND \"LastAppliedReceiptId\" IS NOT NULL AND \"AnonymisedAtUtc\" IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_source_links_observation_shape",
                schema: "ingestion",
                table: "reservation_source_links",
                sql: "((\"LastObservedReceiptId\" = '00000000-0000-0000-0000-000000000000' AND trim(\"LastObservedContentHash\") = '' AND \"LastObservedSourceRevision\" IS NULL AND \"LastObservedSourceSequence\" IS NULL AND \"LastObservedSourceUpdatedAtUtc\" IS NULL) OR (\"LastObservedReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"LastObservedContentHash\") ~ '^[0-9a-f]{64}$' AND (\"LastObservedSourceRevision\" IS NULL OR trim(\"LastObservedSourceRevision\") <> '') AND (\"LastObservedSourceSequence\" IS NULL OR \"LastObservedSourceSequence\" >= 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_anonymised_shape",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "\"AnonymisedAtUtc\" IS NULL OR (\"State\" IN (3, 4, 5, 6, 7) AND \"CompletedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"CompletedAtUtc\" AND \"ReservationId\" IS NULL AND \"SourceRevision\" IS NULL AND \"NormalizedSnapshot\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_coordinates",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"SourceLinkId\" <> '00000000-0000-0000-0000-000000000000' AND \"TriggerId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND (\"ReservationId\" IS NULL OR \"ReservationId\" <> '00000000-0000-0000-0000-000000000000') AND (\"SourceRevision\" IS NULL OR trim(\"SourceRevision\") <> '') AND (\"SourceSequence\" IS NULL OR \"SourceSequence\" >= 0) AND (\"ExpectedDetailsRevision\" IS NULL OR \"ExpectedDetailsRevision\" > 0) AND (\"ResultDetailsRevision\" IS NULL OR \"ResultDetailsRevision\" > 0) AND (\"ResultReservationVersion\" IS NULL OR \"ResultReservationVersion\" > 0) AND (\"ErrorCode\" IS NULL OR trim(\"ErrorCode\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_kind_shape",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "\"TriggerKind\" IN (1, 2) AND \"Kind\" IN (1, 2, 3, 4) AND ((\"Kind\" = 1 AND \"ExpectedDetailsRevision\" IS NULL) OR (\"Kind\" IN (2, 3, 4) AND \"ExpectedDetailsRevision\" IS NOT NULL AND \"ExpectedDetailsRevision\" > 0)) AND (\"Kind\" = 1 OR \"ReservationId\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND NOT (\"State\" = 1 AND \"Kind\" = 1 AND \"ReservationId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "((\"State\" = 1 AND \"Version\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"ResultDetailsRevision\" IS NULL AND \"ResultReservationVersion\" IS NULL AND \"ErrorCode\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 2 AND \"Kind\" = 3 AND \"Version\" >= 2 AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"CreatedAtUtc\" AND \"ReservationId\" IS NOT NULL AND \"ResultDetailsRevision\" IS NOT NULL AND \"ResultReservationVersion\" IS NOT NULL AND \"ErrorCode\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" IN (3, 4) AND \"Version\" >= 2 AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"CreatedAtUtc\" AND (\"ReservationId\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND \"ResultDetailsRevision\" IS NOT NULL AND \"ResultReservationVersion\" IS NOT NULL AND \"ErrorCode\" IS NULL) OR (\"State\" IN (5, 6, 7) AND \"Version\" >= 2 AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"CreatedAtUtc\"))");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "((\"State\" IN (1, 2) AND \"NormalizedSnapshot\" IS NOT NULL AND trim(\"NormalizedSnapshot\") <> '' AND \"SensitiveDataRetainUntilUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" IN (3, 4, 5, 6, 7) AND \"CompletedAtUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" > \"CompletedAtUtc\" AND ((\"NormalizedSnapshot\" IS NOT NULL AND trim(\"NormalizedSnapshot\") <> '' AND \"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"NormalizedSnapshot\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND ((\"AnonymisedAtUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\") OR (\"AnonymisedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\"))))))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_observation_reprocessing_attempts_coordinates",
                schema: "ingestion",
                table: "observation_reprocessing_attempts",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"TaskRunId\" = \"Id\" AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"SourceReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND trim(\"ParserType\") <> '' AND \"ParserVersion\" > 0 AND trim(\"RequestedBy\") <> '' AND \"Version\" >= 1 AND \"LastTaskAttempt\" >= 0 AND \"ReservationExpiresAtUtc\" > \"RequestedAtUtc\" AND (\"LastErrorCode\" IS NULL OR trim(\"LastErrorCode\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_observation_reprocessing_attempts_counters",
                schema: "ingestion",
                table: "observation_reprocessing_attempts",
                sql: "\"ParsedCount\" >= 0 AND \"AcceptedCount\" >= 0 AND \"DuplicateCount\" >= 0 AND \"RejectedCount\" >= 0 AND CAST(\"AcceptedCount\" AS bigint) + \"DuplicateCount\" + \"RejectedCount\" = \"ParsedCount\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_observation_reprocessing_attempts_lifecycle",
                schema: "ingestion",
                table: "observation_reprocessing_attempts",
                sql: "(\"StartedAtUtc\" IS NULL OR \"StartedAtUtc\" >= \"RequestedAtUtc\") AND (\"CompletedAtUtc\" IS NULL OR (\"CompletedAtUtc\" >= \"RequestedAtUtc\" AND (\"StartedAtUtc\" IS NULL OR \"CompletedAtUtc\" >= \"StartedAtUtc\"))) AND ((\"State\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"ParsedCount\" = 0 AND ((\"LastTaskAttempt\" = 0 AND \"StartedAtUtc\" IS NULL AND \"LastErrorCode\" IS NULL AND \"Version\" = 1) OR (\"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND \"LastErrorCode\" IS NOT NULL AND \"Version\" >= 3))) OR (\"State\" = 2 AND \"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NULL AND \"LastErrorCode\" IS NULL AND \"ParsedCount\" = 0 AND \"Version\" >= 2) OR (\"State\" = 3 AND \"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"RejectedCount\" = 0 AND \"LastErrorCode\" IS NULL AND \"Version\" >= 3) OR (\"State\" = 4 AND \"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"ParsedCount\" = 0 AND \"LastErrorCode\" IS NOT NULL AND \"Version\" >= 3) OR (\"State\" = 5 AND \"CompletedAtUtc\" IS NOT NULL AND \"LastErrorCode\" IS NOT NULL AND \"Version\" >= 2) OR (\"State\" IN (6, 7) AND \"CompletedAtUtc\" IS NOT NULL AND \"LastErrorCode\" IS NOT NULL AND \"ParsedCount\" = 0 AND \"Version\" >= 2))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_proposals_anonymised_shape",
                schema: "ingestion",
                table: "change_proposals",
                sql: "\"AnonymisedAtUtc\" IS NULL OR (\"State\" IN (3, 4, 5, 6, 7) AND \"ReservationId\" = '00000000-0000-0000-0000-000000000000' AND \"Diff\" IS NULL AND \"DecisionReason\" IS NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"CompletedAtUtc\" AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_proposals_coordinates",
                schema: "ingestion",
                table: "change_proposals",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReceiptId\" <> '00000000-0000-0000-0000-000000000000' AND \"SourcePayloadFileId\" <> '00000000-0000-0000-0000-000000000000' AND trim(\"ScopeId\") <> '' AND \"BaseReservationDetailsRevision\" > 0 AND (\"ReservationId\" <> '00000000-0000-0000-0000-000000000000' OR \"AnonymisedAtUtc\" IS NOT NULL) AND (\"ProductOperationId\" IS NULL OR \"ProductOperationId\" <> '00000000-0000-0000-0000-000000000000') AND (\"DecisionActor\" IS NULL OR trim(\"DecisionActor\") <> '') AND (\"DecisionReason\" IS NULL OR trim(\"DecisionReason\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_proposals_lifecycle",
                schema: "ingestion",
                table: "change_proposals",
                sql: "((\"State\" = 1 AND \"Version\" = 1 AND \"DecisionActor\" IS NULL AND \"DecisionReason\" IS NULL AND \"ProductOperationId\" IS NULL AND \"DecidedAtUtc\" IS NULL AND \"CompletedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 2 AND \"Version\" = 2 AND \"DecisionActor\" IS NOT NULL AND \"DecisionReason\" IS NULL AND \"ProductOperationId\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL AND \"DecidedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" = 3 AND \"Version\" >= 3 AND \"DecisionActor\" IS NOT NULL AND \"DecisionReason\" IS NULL AND \"ProductOperationId\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"DecidedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" >= \"DecidedAtUtc\") OR (\"State\" IN (4, 5) AND \"Version\" >= 2 AND \"DecisionActor\" IS NOT NULL AND (\"DecisionReason\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND \"ProductOperationId\" IS NULL AND \"DecidedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" = \"DecidedAtUtc\" AND \"DecidedAtUtc\" >= \"CreatedAtUtc\") OR (\"State\" IN (6, 7) AND \"Version\" >= 3 AND \"DecisionActor\" IS NOT NULL AND (\"DecisionReason\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND \"ProductOperationId\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"DecidedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" >= \"DecidedAtUtc\"))");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_proposals_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "change_proposals");

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_proposals_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "change_proposals",
                sql: "((\"State\" IN (1, 2) AND \"Diff\" IS NOT NULL AND trim(\"Diff\") <> '' AND \"SensitiveDataRetainUntilUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"State\" IN (3, 4, 5, 6, 7) AND \"CompletedAtUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" > \"CompletedAtUtc\" AND ((\"Diff\" IS NOT NULL AND trim(\"Diff\") <> '' AND \"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR (\"Diff\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND ((\"AnonymisedAtUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\") OR (\"AnonymisedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\"))))))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM ingestion.change_proposals
                        WHERE "AnonymisedAtUtc" IS NOT NULL
                          AND "SensitiveDataRedactedAtUtc" < "SensitiveDataRetainUntilUtc")
                       OR EXISTS (
                        SELECT 1
                        FROM ingestion.reservation_dispatches
                        WHERE "AnonymisedAtUtc" IS NOT NULL
                          AND "SensitiveDataRedactedAtUtc" < "SensitiveDataRetainUntilUtc") THEN
                        RAISE EXCEPTION
                            'Cannot downgrade ingestion source-graph integrity after early anonymisation';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_source_links_anonymised_shape",
                schema: "ingestion",
                table: "reservation_source_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_source_links_applied_shape",
                schema: "ingestion",
                table: "reservation_source_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_source_links_coordinates",
                schema: "ingestion",
                table: "reservation_source_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_source_links_lifecycle",
                schema: "ingestion",
                table: "reservation_source_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_source_links_observation_shape",
                schema: "ingestion",
                table: "reservation_source_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_anonymised_shape",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_coordinates",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_kind_shape",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservation_dispatches_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservation_dispatches_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "reservation_dispatches",
                sql: "(\"State\" IN (1, 2) AND \"NormalizedSnapshot\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL) OR (\"State\" IN (3, 4, 5, 6, 7) AND \"SensitiveDataRetainUntilUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" > COALESCE(\"CompletedAtUtc\", \"CreatedAtUtc\") AND ((\"NormalizedSnapshot\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL) OR (\"NormalizedSnapshot\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\")))");

            migrationBuilder.DropCheckConstraint(
                name: "CK_observation_reprocessing_attempts_coordinates",
                schema: "ingestion",
                table: "observation_reprocessing_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_observation_reprocessing_attempts_counters",
                schema: "ingestion",
                table: "observation_reprocessing_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_observation_reprocessing_attempts_lifecycle",
                schema: "ingestion",
                table: "observation_reprocessing_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_proposals_anonymised_shape",
                schema: "ingestion",
                table: "change_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_proposals_coordinates",
                schema: "ingestion",
                table: "change_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_proposals_lifecycle",
                schema: "ingestion",
                table: "change_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_proposals_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "change_proposals");

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_proposals_sensitive_history_lifecycle",
                schema: "ingestion",
                table: "change_proposals",
                sql: "(\"State\" IN (1, 2) AND \"Diff\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL) OR (\"State\" IN (3, 4, 5, 6, 7) AND \"SensitiveDataRetainUntilUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" > COALESCE(\"CompletedAtUtc\", \"DecidedAtUtc\", \"CreatedAtUtc\") AND ((\"Diff\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL) OR (\"Diff\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND \"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\")))");
        }
    }
}
