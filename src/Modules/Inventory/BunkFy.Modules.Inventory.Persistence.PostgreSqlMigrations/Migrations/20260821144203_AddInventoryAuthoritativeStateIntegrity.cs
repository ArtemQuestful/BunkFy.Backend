using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Inventory.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAuthoritativeStateIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_room_retirements_coordinates",
                schema: "inventory",
                table: "room_retirements",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"RoomId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_room_retirements_lifecycle",
                schema: "inventory",
                table: "room_retirements",
                sql: "(\"State\" = 1 AND \"Version\" = 1 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NULL AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 2 AND \"Version\" >= 2 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 3 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 4 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" = \"UpdatedAtUtc\" AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 5 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NOT NULL AND \"RejectionReasonCode\" > 0 AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 6 AND \"Version\" = 2 AND \"RejectionReasonCode\" IS NULL AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NOT NULL AND \"CanceledBy\" IS NOT NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"CanceledAtUtc\" IS NOT NULL AND length(trim(\"CancellationReason\")) > 0 AND length(trim(\"CanceledBy\")) > 0 AND \"CanceledAtUtc\" = \"UpdatedAtUtc\" AND \"CanceledAtUtc\" >= \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_room_retirements_request",
                schema: "inventory",
                table: "room_retirements",
                sql: "length(trim(\"Reason\")) > 0 AND length(trim(\"RequestedBy\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_room_configurations_coordinates",
                schema: "inventory",
                table: "room_configurations",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_room_configurations_state",
                schema: "inventory",
                table: "room_configurations",
                sql: "\"AvailabilityMutationVersion\" >= \"Version\" AND ((\"Version\" = 1 AND \"SalesMode\" = 1 AND \"UpdatedAtUtc\" IS NULL) OR (\"Version\" >= 2 AND \"SalesMode\" IN (2, 3) AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_manual_blocks_content",
                schema: "inventory",
                table: "manual_blocks",
                sql: "\"Arrival\" < \"Departure\" AND length(trim(\"Reason\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_manual_blocks_coordinates",
                schema: "inventory",
                table: "manual_blocks",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"BlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"InventoryUnitId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_manual_blocks_lifecycle",
                schema: "inventory",
                table: "manual_blocks",
                sql: "(\"Status\" = 1 AND \"Version\" = 1 AND \"ReleasedAtUtc\" IS NULL) OR (\"Status\" = 2 AND \"Version\" = 2 AND \"ReleasedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" >= \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bed_retirements_coordinates",
                schema: "inventory",
                table: "bed_retirements",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"RoomId\" <> '00000000-0000-0000-0000-000000000000' AND \"BedId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bed_retirements_lifecycle",
                schema: "inventory",
                table: "bed_retirements",
                sql: "(\"State\" = 1 AND \"Version\" = 1 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NULL AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 2 AND \"Version\" >= 2 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 3 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 4 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" = \"UpdatedAtUtc\" AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 5 AND \"Version\" >= 3 AND \"RejectionReasonCode\" IS NOT NULL AND \"RejectionReasonCode\" > 0 AND \"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND \"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR (\"State\" = 6 AND \"Version\" = 2 AND \"RejectionReasonCode\" IS NULL AND \"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NOT NULL AND \"CanceledBy\" IS NOT NULL AND \"UpdatedAtUtc\" IS NOT NULL AND \"CanceledAtUtc\" IS NOT NULL AND length(trim(\"CancellationReason\")) > 0 AND length(trim(\"CanceledBy\")) > 0 AND \"CanceledAtUtc\" = \"UpdatedAtUtc\" AND \"CanceledAtUtc\" >= \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bed_retirements_request",
                schema: "inventory",
                table: "bed_retirements",
                sql: "length(trim(\"Reason\")) > 0 AND length(trim(\"RequestedBy\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocations_coordinates",
                schema: "inventory",
                table: "allocations",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"AllocationRequestId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocations_lifecycle",
                schema: "inventory",
                table: "allocations",
                sql: "(\"Status\" = 1 AND \"Rejection\" = 0 AND \"ReleaseRequestId\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR (\"Status\" = 2 AND \"Rejection\" BETWEEN 1 AND 6 AND \"ReleaseRequestId\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR (\"Status\" = 3 AND \"Rejection\" = 0 AND \"ReleaseRequestId\" IS NOT NULL AND \"ReleaseRequestId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReleasedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" >= \"CreatedAtUtc\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocations_stay_and_version",
                schema: "inventory",
                table: "allocations",
                sql: "\"Arrival\" < \"Departure\" AND \"Version\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_units_coordinates",
                schema: "inventory",
                table: "allocation_units",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"AllocationId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_operation_locks_coordinates",
                schema: "inventory",
                table: "allocation_operation_locks",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"Id\" = \"AllocationId\" AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"AllocationId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_fingerprint",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "length(\"RequestFingerprint\") = 64 AND \"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_outcome",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "(\"Confirmed\" = TRUE AND \"RejectionReason\" IS NULL AND \"AllocationVersion\" IS NOT NULL AND \"AllocationVersion\" >= 1) OR (\"Confirmed\" = FALSE AND \"RejectionReason\" IS NOT NULL AND \"RejectionReason\" BETWEEN 1 AND 11 AND \"AllocationVersion\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_room_retirements_coordinates",
                schema: "inventory",
                table: "room_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_room_retirements_lifecycle",
                schema: "inventory",
                table: "room_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_room_retirements_request",
                schema: "inventory",
                table: "room_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_room_configurations_coordinates",
                schema: "inventory",
                table: "room_configurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_room_configurations_state",
                schema: "inventory",
                table: "room_configurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_manual_blocks_content",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_manual_blocks_coordinates",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_manual_blocks_lifecycle",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bed_retirements_coordinates",
                schema: "inventory",
                table: "bed_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bed_retirements_lifecycle",
                schema: "inventory",
                table: "bed_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bed_retirements_request",
                schema: "inventory",
                table: "bed_retirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocations_coordinates",
                schema: "inventory",
                table: "allocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocations_lifecycle",
                schema: "inventory",
                table: "allocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocations_stay_and_version",
                schema: "inventory",
                table: "allocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_units_coordinates",
                schema: "inventory",
                table: "allocation_units");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_operation_locks_coordinates",
                schema: "inventory",
                table: "allocation_operation_locks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_fingerprint",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_outcome",
                schema: "inventory",
                table: "allocation_amendment_decisions");
        }
    }
}
