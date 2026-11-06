using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Reservations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationStayAmendmentConvergence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpLockSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_business_date",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_expected_revision",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_kind",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_request_fingerprint",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.AddColumn<Guid>(
                name: "PendingInventoryAmendmentRequestId",
                schema: "reservations",
                table: "reservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stay_amendment_operations",
                schema: "reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    TargetArrival = table.Column<DateOnly>(type: "date", nullable: true),
                    TargetDeparture = table.Column<DateOnly>(type: "date", nullable: true),
                    TargetExpectedArrivalTime = table.Column<TimeOnly>(type: "time(0) without time zone", precision: 0, nullable: true),
                    TargetExpectedDepartureTime = table.Column<TimeOnly>(type: "time(0) without time zone", precision: 0, nullable: true),
                    TargetInventoryUnitIds = table.Column<string>(type: "character varying(3300)", maxLength: 3300, nullable: true),
                    ExpectedDetailsRevision = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    OperationVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultingDetailsRevision = table.Column<long>(type: "bigint", nullable: true),
                    ResultingReservationVersion = table.Column<long>(type: "bigint", nullable: true),
                    ResultingAllocationVersion = table.Column<long>(type: "bigint", nullable: true),
                    RejectionCode = table.Column<int>(type: "integer", nullable: true),
                    ReconciliationCount = table.Column<int>(type: "integer", nullable: false),
                    LastReconciledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReconciledBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stay_amendment_operations", x => new { x.ScopeId, x.ReservationId, x.Id });
                    table.CheckConstraint("CK_stay_amendment_operations_outcome", "(\"Outcome\" = 1 AND \"CompletedAtUtc\" IS NULL AND \"ResultingDetailsRevision\" IS NULL AND \"ResultingReservationVersion\" IS NULL AND \"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" IS NULL) OR (\"Outcome\" = 2 AND \"CompletedAtUtc\" IS NOT NULL AND \"ResultingDetailsRevision\" >= 1 AND \"ResultingReservationVersion\" >= 1 AND \"ResultingAllocationVersion\" >= 1 AND \"RejectionCode\" IS NULL) OR (\"Outcome\" = 3 AND \"CompletedAtUtc\" IS NOT NULL AND \"ResultingDetailsRevision\" >= 1 AND \"ResultingReservationVersion\" >= 1 AND \"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" >= 1) OR (\"Outcome\" = 4 AND \"CompletedAtUtc\" IS NULL AND \"ResultingDetailsRevision\" IS NULL AND \"ResultingReservationVersion\" IS NULL AND \"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" IS NULL AND \"ReconciliationCount\" = 0)");
                    table.CheckConstraint("CK_stay_amendment_operations_reconciliation", "(\"ReconciliationCount\" = 0 AND \"LastReconciledAtUtc\" IS NULL AND \"LastReconciledBy\" IS NULL) OR (\"ReconciliationCount\" >= 1 AND \"LastReconciledAtUtc\" IS NOT NULL AND \"LastReconciledBy\" IS NOT NULL AND \"LastReconciledBy\" = trim(\"LastReconciledBy\") AND char_length(\"LastReconciledBy\") > 0 AND \"LastReconciledBy\" !~ '[[:cntrl:]]')");
                    table.CheckConstraint("CK_stay_amendment_operations_request", "\"RequestSchemaVersion\" IN (1, 2) AND char_length(\"RequestFingerprint\") = 64 AND \"RequestFingerprint\" ~ '^[0-9a-f]{64}$' AND \"ExpectedDetailsRevision\" >= 1 AND ((\"Outcome\" = 4 AND \"RequestSchemaVersion\" = 1 AND \"RequestedBy\" IS NULL AND \"InventoryRequestId\" IS NULL) OR (\"Outcome\" = 1 AND \"InventoryRequestId\" IS NOT NULL AND \"RequestedBy\" IS NOT NULL AND \"RequestedBy\" = trim(\"RequestedBy\") AND char_length(\"RequestedBy\") > 0 AND \"RequestedBy\" !~ '[[:cntrl:]]') OR (\"Outcome\" IN (2, 3) AND ((\"OperationVersion\" = 1 AND \"InventoryRequestId\" IS NULL) OR (\"OperationVersion\" > 1 AND \"InventoryRequestId\" IS NOT NULL)) AND \"RequestedBy\" IS NOT NULL AND \"RequestedBy\" = trim(\"RequestedBy\") AND char_length(\"RequestedBy\") > 0 AND \"RequestedBy\" !~ '[[:cntrl:]]'))");
                    table.CheckConstraint("CK_stay_amendment_operations_target", "(\"Outcome\" = 4 AND \"TargetArrival\" IS NULL AND \"TargetDeparture\" IS NULL AND \"TargetExpectedArrivalTime\" IS NULL AND \"TargetExpectedDepartureTime\" IS NULL AND \"TargetInventoryUnitIds\" IS NULL) OR (\"Outcome\" IN (1, 2, 3) AND \"TargetArrival\" IS NOT NULL AND \"TargetDeparture\" IS NOT NULL AND \"TargetArrival\" < \"TargetDeparture\" AND (\"TargetExpectedArrivalTime\" IS NULL OR date_part('second', \"TargetExpectedArrivalTime\") = 0) AND (\"TargetExpectedDepartureTime\" IS NULL OR date_part('second', \"TargetExpectedDepartureTime\") = 0) AND \"TargetInventoryUnitIds\" IS NOT NULL AND \"TargetInventoryUnitIds\" ~ '^[0-9a-f]{32}(,[0-9a-f]{32}){0,99}$')");
                    table.CheckConstraint("CK_stay_amendment_operations_timestamps", "\"UpdatedAtUtc\" >= \"RequestedAtUtc\" AND (\"CompletedAtUtc\" IS NULL OR (\"CompletedAtUtc\" >= \"RequestedAtUtc\" AND \"CompletedAtUtc\" <= \"UpdatedAtUtc\")) AND (\"LastReconciledAtUtc\" IS NULL OR (\"LastReconciledAtUtc\" >= \"RequestedAtUtc\" AND \"LastReconciledAtUtc\" <= \"UpdatedAtUtc\"))");
                    table.CheckConstraint("CK_stay_amendment_operations_version", "(\"Outcome\" = 1 AND \"OperationVersion\" = \"ReconciliationCount\" + 1) OR (\"Outcome\" = 2 AND ((\"OperationVersion\" = 1 AND \"ReconciliationCount\" = 0) OR \"OperationVersion\" = \"ReconciliationCount\" + 2)) OR (\"Outcome\" = 3 AND \"OperationVersion\" = \"ReconciliationCount\" + 2) OR (\"Outcome\" = 4 AND \"OperationVersion\" = 1 AND \"ReconciliationCount\" = 0)");
                    table.ForeignKey(
                        name: "FK_stay_amendment_operations_management_operations_ScopeId_Res~",
                        columns: x => new { x.ScopeId, x.ReservationId, x.Id },
                        principalSchema: "reservations",
                        principalTable: "management_operations",
                        principalColumns: new[] { "ScopeId", "ReservationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(UpBackfillSql);

            migrationBuilder.CreateIndex(
                name: "IX_reservations_PendingInventoryAmendmentRequestId",
                schema: "reservations",
                table: "reservations",
                column: "PendingInventoryAmendmentRequestId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservations_pending_inventory_request",
                schema: "reservations",
                table: "reservations",
                sql: "(\"PendingAllocationAmendmentId\" IS NULL AND \"PendingInventoryAmendmentRequestId\" IS NULL) OR (\"PendingAllocationAmendmentId\" IS NOT NULL AND \"PendingInventoryAmendmentRequestId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_business_date",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 5, 6, 7) AND \"BusinessDate\" IS NULL) OR (\"Kind\" IN (2, 3, 4) AND \"BusinessDate\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_expected_revision",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 2, 3, 4) AND \"ExpectedVersion\" > 0 AND \"ExpectedDetailsRevision\" IS NULL) OR (\"Kind\" IN (5, 6, 7) AND \"ExpectedVersion\" IS NULL AND \"ExpectedDetailsRevision\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_kind",
                schema: "reservations",
                table: "management_operations",
                sql: "\"Kind\" IN (1, 2, 3, 4, 5, 6, 7)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_request_fingerprint",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 2, 3, 4, 5) AND \"RequestFingerprint\" IS NULL) OR (\"Kind\" IN (6, 7) AND \"RequestFingerprint\" IS NOT NULL AND char_length(\"RequestFingerprint\") = 64 AND \"RequestFingerprint\" ~ '^[0-9a-f]{64}$')");

            migrationBuilder.CreateIndex(
                name: "IX_stay_amendment_operations_InventoryRequestId",
                schema: "reservations",
                table: "stay_amendment_operations",
                column: "InventoryRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stay_amendment_operations_ScopeId_PropertyId_Outcome_Update~",
                schema: "reservations",
                table: "stay_amendment_operations",
                columns: new[] { "ScopeId", "PropertyId", "Outcome", "UpdatedAtUtc", "Id", "ReservationId" });

            migrationBuilder.Sql(UpProtocolSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);

            migrationBuilder.DropTable(
                name: "stay_amendment_operations",
                schema: "reservations");

            migrationBuilder.DropIndex(
                name: "IX_reservations_PendingInventoryAmendmentRequestId",
                schema: "reservations",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservations_pending_inventory_request",
                schema: "reservations",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_business_date",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_expected_revision",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_kind",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_management_operations_request_fingerprint",
                schema: "reservations",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "PendingInventoryAmendmentRequestId",
                schema: "reservations",
                table: "reservations");

            migrationBuilder.Sql(DownCleanupSql);

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_business_date",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 5, 6) AND \"BusinessDate\" IS NULL) OR (\"Kind\" IN (2, 3, 4) AND \"BusinessDate\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_expected_revision",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 2, 3, 4) AND \"ExpectedVersion\" > 0 AND \"ExpectedDetailsRevision\" IS NULL) OR (\"Kind\" IN (5, 6) AND \"ExpectedVersion\" IS NULL AND \"ExpectedDetailsRevision\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_kind",
                schema: "reservations",
                table: "management_operations",
                sql: "\"Kind\" IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_management_operations_request_fingerprint",
                schema: "reservations",
                table: "management_operations",
                sql: "(\"Kind\" IN (1, 2, 3, 4, 5) AND \"RequestFingerprint\" IS NULL) OR (\"Kind\" = 6 AND \"RequestFingerprint\" IS NOT NULL AND char_length(\"RequestFingerprint\") = 64 AND \"RequestFingerprint\" ~ '^[0-9a-f]{64}$')");
        }
    }
}
