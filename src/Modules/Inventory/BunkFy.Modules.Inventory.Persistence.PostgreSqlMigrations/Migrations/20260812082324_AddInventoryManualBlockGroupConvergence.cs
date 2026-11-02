using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Inventory.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryManualBlockGroupConvergence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpLockAndPreflightSql);

            migrationBuilder.DropForeignKey(
                name: "FK_manual_blocks_inventory_units_ScopeId_InventoryUnitId",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_tenant_destroy_operation_progress",
                schema: "inventory",
                table: "tenant_destroy_operations");

            migrationBuilder.DropIndex(
                name: "IX_manual_blocks_ScopeId_InventoryUnitId",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_management_operations_kind",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_management_operations_result",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_inventory_units_ScopeId_Id",
                schema: "inventory",
                table: "inventory_units");

            migrationBuilder.AddColumn<long>(
                name: "AvailabilitySelectionVersion",
                schema: "inventory",
                table: "property_topology",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<int>(
                name: "ResultActiveBlockCount",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultAlreadyReleasedBlockCount",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultBlockGroupStatus",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultCreatedBlockCount",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultMembershipDigest",
                schema: "inventory",
                table: "management_operations",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResultPreviousBlockGroupId",
                schema: "inventory",
                table: "management_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultReleasedBlockCount",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultTotalBlockCount",
                schema: "inventory",
                table: "management_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_inventory_units_ScopeId_PropertyId_Id",
                schema: "inventory",
                table: "inventory_units",
                columns: new[] { "ScopeId", "PropertyId", "Id" });

            migrationBuilder.CreateTable(
                name: "manual_block_groups",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    BuildingLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FloorLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    InventoryUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Arrival = table.Column<DateOnly>(type: "date", nullable: false),
                    Departure = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SelectionDigest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    MembershipDigest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    MembershipDigestVersion = table.Column<int>(type: "integer", nullable: false),
                    InitialBlockCount = table.Column<int>(type: "integer", nullable: false),
                    ActiveBlockCount = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ReplacesGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastModifiedByActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_manual_block_groups", x => new { x.ScopeId, x.PropertyId, x.Id });
                    table.CheckConstraint("CK_inventory_manual_block_groups_actors", "((\"TargetKind\" = 0 AND \"CreatedByActorId\" IS NULL AND (\"LastModifiedByActorId\" IS NULL OR (char_length(btrim(\"LastModifiedByActorId\")) > 0 AND \"LastModifiedByActorId\" = btrim(\"LastModifiedByActorId\") AND \"LastModifiedByActorId\" !~ '[[:cntrl:]]'))) OR (\"TargetKind\" BETWEEN 1 AND 5 AND \"CreatedByActorId\" IS NOT NULL AND char_length(btrim(\"CreatedByActorId\")) > 0 AND \"CreatedByActorId\" = btrim(\"CreatedByActorId\") AND \"CreatedByActorId\" !~ '[[:cntrl:]]' AND \"LastModifiedByActorId\" IS NOT NULL AND char_length(btrim(\"LastModifiedByActorId\")) > 0 AND \"LastModifiedByActorId\" = btrim(\"LastModifiedByActorId\") AND \"LastModifiedByActorId\" !~ '[[:cntrl:]]'))");
                    table.CheckConstraint("CK_inventory_manual_block_groups_coordinates", "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND char_length(btrim(\"ScopeId\")) > 0 AND (\"ReplacesGroupId\" IS NULL OR (\"ReplacesGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReplacesGroupId\" <> \"Id\"))");
                    table.CheckConstraint("CK_inventory_manual_block_groups_counts", "\"InitialBlockCount\" > 0 AND \"ActiveBlockCount\" BETWEEN 0 AND \"InitialBlockCount\" AND (\"InitialBlockCount\" <= 500 OR (\"TargetKind\" = 0 AND \"State\" = 3 AND \"ActiveBlockCount\" = 0))");
                    table.CheckConstraint("CK_inventory_manual_block_groups_digests", "(\"SelectionDigest\" IS NULL OR (char_length(\"SelectionDigest\") = 64 AND \"SelectionDigest\" ~ '^[0-9a-f]{64}$')) AND char_length(\"MembershipDigest\") = 64 AND \"MembershipDigest\" ~ '^[0-9a-f]{64}$' AND \"MembershipDigestVersion\" = 1");
                    table.CheckConstraint("CK_inventory_manual_block_groups_lifecycle", "\"Version\" > 0 AND (\"UpdatedAtUtc\" IS NULL OR \"UpdatedAtUtc\" >= \"CreatedAtUtc\") AND (\"ReleasedAtUtc\" IS NULL OR \"ReleasedAtUtc\" >= \"CreatedAtUtc\") AND ((\"State\" = 1 AND \"ActiveBlockCount\" = \"InitialBlockCount\" AND \"UpdatedAtUtc\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR (\"State\" = 2 AND \"ActiveBlockCount\" > 0 AND \"ActiveBlockCount\" < \"InitialBlockCount\" AND \"UpdatedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" IS NULL) OR (\"State\" IN (3, 4) AND \"ActiveBlockCount\" = 0 AND \"UpdatedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" = \"UpdatedAtUtc\"))");
                    table.CheckConstraint("CK_inventory_manual_block_groups_range", "\"Arrival\" < \"Departure\"");
                    table.CheckConstraint("CK_inventory_manual_block_groups_target", "(\"TargetKind\" = 0 AND \"SelectionDigest\" IS NULL AND \"BuildingLabel\" IS NULL AND \"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND \"InventoryUnitId\" IS NULL) OR (\"TargetKind\" = 1 AND \"SelectionDigest\" IS NOT NULL AND \"BuildingLabel\" IS NULL AND \"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND \"InventoryUnitId\" IS NULL) OR (\"TargetKind\" = 2 AND \"SelectionDigest\" IS NOT NULL AND \"BuildingLabel\" IS NOT NULL AND char_length(btrim(\"BuildingLabel\")) > 0 AND \"BuildingLabel\" = btrim(\"BuildingLabel\") AND \"BuildingLabel\" !~ '[[:cntrl:]]' AND \"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND \"InventoryUnitId\" IS NULL) OR (\"TargetKind\" = 3 AND \"SelectionDigest\" IS NOT NULL AND \"FloorLabel\" IS NOT NULL AND char_length(btrim(\"FloorLabel\")) > 0 AND \"FloorLabel\" = btrim(\"FloorLabel\") AND \"FloorLabel\" !~ '[[:cntrl:]]' AND (\"BuildingLabel\" IS NULL OR (char_length(btrim(\"BuildingLabel\")) > 0 AND \"BuildingLabel\" = btrim(\"BuildingLabel\") AND \"BuildingLabel\" !~ '[[:cntrl:]]')) AND \"RoomId\" IS NULL AND \"InventoryUnitId\" IS NULL) OR (\"TargetKind\" = 4 AND \"SelectionDigest\" IS NOT NULL AND \"BuildingLabel\" IS NULL AND \"FloorLabel\" IS NULL AND \"RoomId\" IS NOT NULL AND \"RoomId\" <> '00000000-0000-0000-0000-000000000000' AND \"InventoryUnitId\" IS NULL) OR (\"TargetKind\" = 5 AND \"SelectionDigest\" IS NOT NULL AND \"BuildingLabel\" IS NULL AND \"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND \"InventoryUnitId\" IS NOT NULL AND \"InventoryUnitId\" <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("CK_inventory_manual_block_groups_text", "char_length(\"Reason\") BETWEEN 1 AND 500 AND \"Reason\" = btrim(\"Reason\") AND \"Reason\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_inventory_manual_block_groups_replaces",
                        columns: x => new { x.ScopeId, x.PropertyId, x.ReplacesGroupId },
                        principalSchema: "inventory",
                        principalTable: "manual_block_groups",
                        principalColumns: new[] { "ScopeId", "PropertyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(UpBackfillSql);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_tenant_destroy_operation_progress",
                schema: "inventory",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 21 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_manual_blocks_group_unit",
                schema: "inventory",
                table: "manual_blocks",
                columns: new[] { "ScopeId", "PropertyId", "BlockGroupId", "InventoryUnitId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_management_operations_group_v2_shape",
                schema: "inventory",
                table: "management_operations",
                sql: "((\"Kind\" BETWEEN 1 AND 11 AND \"ResultBlockGroupStatus\" IS NULL AND \"ResultPreviousBlockGroupId\" IS NULL AND \"ResultTotalBlockCount\" IS NULL AND \"ResultActiveBlockCount\" IS NULL AND \"ResultReleasedBlockCount\" IS NULL AND \"ResultAlreadyReleasedBlockCount\" IS NULL AND \"ResultCreatedBlockCount\" IS NULL AND \"ResultMembershipDigest\" IS NULL) OR (\"Kind\" = 12 AND \"ResultBlockGroupStatus\" = 1 AND \"ResultPreviousBlockGroupId\" IS NULL AND \"ResultTotalBlockCount\" BETWEEN 1 AND 500 AND \"ResultActiveBlockCount\" = \"ResultTotalBlockCount\" AND \"ResultReleasedBlockCount\" = 0 AND \"ResultAlreadyReleasedBlockCount\" = 0 AND \"ResultCreatedBlockCount\" = \"ResultTotalBlockCount\" AND \"ResultAffectedBlockCount\" = \"ResultTotalBlockCount\" AND \"ResultVersion\" = 1 AND \"ResultMembershipDigest\" IS NOT NULL) OR (\"Kind\" = 13 AND \"ResultPreviousBlockGroupId\" = \"ResourceId\" AND \"ResultTotalBlockCount\" BETWEEN 1 AND 500 AND \"ResultActiveBlockCount\" > 0 AND \"ResultReleasedBlockCount\" >= 0 AND \"ResultAlreadyReleasedBlockCount\" >= 0 AND \"ResultCreatedBlockCount\" >= 0 AND \"ResultMembershipDigest\" IS NOT NULL AND ((\"ResultBlockGroupId\" = \"ResourceId\" AND \"ResultVersion\" = \"ExpectedVersion\" AND \"ResultAffectedBlockCount\" = 0 AND \"ResultReleasedBlockCount\" = 0 AND \"ResultCreatedBlockCount\" = 0 AND \"ResultTotalBlockCount\" = \"ResultActiveBlockCount\" + \"ResultAlreadyReleasedBlockCount\" AND ((\"ResultBlockGroupStatus\" = 1 AND \"ResultActiveBlockCount\" = \"ResultTotalBlockCount\") OR (\"ResultBlockGroupStatus\" = 2 AND \"ResultActiveBlockCount\" < \"ResultTotalBlockCount\"))) OR (\"ResultBlockGroupId\" <> \"ResourceId\" AND \"ResultVersion\" = 1 AND \"ResultBlockGroupStatus\" = 1 AND \"ResultActiveBlockCount\" = \"ResultTotalBlockCount\" AND \"ResultCreatedBlockCount\" = \"ResultTotalBlockCount\" AND \"ResultReleasedBlockCount\" > 0 AND \"ResultAffectedBlockCount\" = \"ResultReleasedBlockCount\" + \"ResultCreatedBlockCount\"))) OR (\"Kind\" = 14 AND \"ResultPreviousBlockGroupId\" IS NULL AND \"ResultBlockGroupId\" = \"ResourceId\" AND \"ResultTotalBlockCount\" > 0 AND \"ResultActiveBlockCount\" = 0 AND \"ResultCreatedBlockCount\" = 0 AND \"ResultMembershipDigest\" IS NOT NULL AND ((\"ResultVersion\" = \"ExpectedVersion\" AND \"ResultBlockGroupStatus\" IN (3, 4) AND \"ResultAffectedBlockCount\" = 0 AND \"ResultReleasedBlockCount\" = 0 AND \"ResultAlreadyReleasedBlockCount\" = \"ResultTotalBlockCount\") OR (\"ResultVersion\" = \"ExpectedVersion\" + 1 AND \"ResultBlockGroupStatus\" = 3 AND \"ResultReleasedBlockCount\" > 0 AND \"ResultAffectedBlockCount\" = \"ResultReleasedBlockCount\" AND \"ResultTotalBlockCount\" = \"ResultReleasedBlockCount\" + \"ResultAlreadyReleasedBlockCount\" AND \"ResultTotalBlockCount\" <= 500))))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_management_operations_kind",
                schema: "inventory",
                table: "management_operations",
                sql: "\"Kind\" BETWEEN 1 AND 14 AND ((\"Kind\" = 1 AND \"ResourceKind\" = 1) OR (\"Kind\" IN (2, 3) AND \"ResourceKind\" = 2 AND \"ResourceId\" = \"PropertyId\") OR (\"Kind\" = 4 AND \"ResourceKind\" = 3 AND \"ResultBlockId\" IS NOT NULL AND \"ResourceId\" = \"ResultBlockId\") OR (\"Kind\" = 5 AND \"ResourceKind\" = 4 AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResourceId\" = \"ResultBlockGroupId\") OR (\"Kind\" = 6 AND \"ResourceKind\" = 5) OR (\"Kind\" = 7 AND \"ResourceKind\" = 6 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 8 AND \"ResourceKind\" = 1) OR (\"Kind\" = 9 AND \"ResourceKind\" = 7 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 10 AND \"ResourceKind\" = 6 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 11 AND \"ResourceKind\" = 7 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 12 AND \"ResourceKind\" = 2 AND \"ResourceId\" = \"PropertyId\") OR (\"Kind\" = 13 AND \"ResourceKind\" = 4 AND \"ResourceId\" = \"ResultPreviousBlockGroupId\") OR (\"Kind\" = 14 AND \"ResourceKind\" = 4 AND \"ResourceId\" = \"ResultBlockGroupId\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_management_operations_result",
                schema: "inventory",
                table: "management_operations",
                sql: "(\"Kind\" = 1 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" BETWEEN \"ExpectedVersion\" AND \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NOT NULL AND \"ResultSalesMode\" IN (2, 3) AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 2 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NOT NULL AND \"ResultBlockId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NOT NULL AND \"ResultBlockStatus\" = 1 AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" = 1 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 3 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" > 0 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 4 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NOT NULL AND \"ResultBlockId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NOT NULL AND \"ResultBlockStatus\" = 2 AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" = 1 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 5 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" > 0 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" IN (6, 8) AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" > 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NOT NULL AND \"ResultTopologyChangeId\" <> '00000000-0000-0000-0000-000000000000') OR (\"Kind\" IN (7, 9, 10, 11) AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NOT NULL AND \"ResultTopologyChangeId\" <> '00000000-0000-0000-0000-000000000000') OR (\"Kind\" IN (12, 13, 14) AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" >= 0 AND \"ResultTopologyChangeId\" IS NULL AND \"ResultBlockGroupStatus\" IS NOT NULL AND \"ResultBlockGroupStatus\" BETWEEN 1 AND 4 AND \"ResultTotalBlockCount\" IS NOT NULL AND \"ResultTotalBlockCount\" > 0 AND \"ResultActiveBlockCount\" IS NOT NULL AND \"ResultActiveBlockCount\" BETWEEN 0 AND \"ResultTotalBlockCount\" AND \"ResultReleasedBlockCount\" IS NOT NULL AND \"ResultReleasedBlockCount\" >= 0 AND \"ResultAlreadyReleasedBlockCount\" IS NOT NULL AND \"ResultAlreadyReleasedBlockCount\" >= 0 AND \"ResultCreatedBlockCount\" IS NOT NULL AND \"ResultCreatedBlockCount\" >= 0 AND char_length(\"ResultMembershipDigest\") = 64 AND \"ResultMembershipDigest\" ~ '^[0-9a-f]{64}$' AND ((\"Kind\" = 12 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 1 AND \"ResultPreviousBlockGroupId\" IS NULL) OR (\"Kind\" = 13 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" > 0 AND \"ResultPreviousBlockGroupId\" IS NOT NULL AND \"ResultPreviousBlockGroupId\" <> '00000000-0000-0000-0000-000000000000') OR (\"Kind\" = 14 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" > 0 AND \"ResultPreviousBlockGroupId\" IS NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_manual_block_groups_property_created",
                schema: "inventory",
                table: "manual_block_groups",
                columns: new[] { "ScopeId", "PropertyId", "CreatedAtUtc", "Id" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_manual_block_groups_property_state_created",
                schema: "inventory",
                table: "manual_block_groups",
                columns: new[] { "ScopeId", "PropertyId", "State", "CreatedAtUtc", "Id" },
                descending: new[] { false, false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "UX_inventory_manual_block_groups_replaces",
                schema: "inventory",
                table: "manual_block_groups",
                columns: new[] { "ScopeId", "PropertyId", "ReplacesGroupId" },
                unique: true,
                filter: "\"ReplacesGroupId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_manual_blocks_group",
                schema: "inventory",
                table: "manual_blocks",
                columns: new[] { "ScopeId", "PropertyId", "BlockGroupId" },
                principalSchema: "inventory",
                principalTable: "manual_block_groups",
                principalColumns: new[] { "ScopeId", "PropertyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_manual_blocks_inventory_unit",
                schema: "inventory",
                table: "manual_blocks",
                columns: new[] { "ScopeId", "PropertyId", "InventoryUnitId" },
                principalSchema: "inventory",
                principalTable: "inventory_units",
                principalColumns: new[] { "ScopeId", "PropertyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(UpProtocolSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_manual_blocks_group",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropForeignKey(
                name: "FK_inventory_manual_blocks_inventory_unit",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropTable(
                name: "manual_block_groups",
                schema: "inventory");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_tenant_destroy_operation_progress",
                schema: "inventory",
                table: "tenant_destroy_operations");

            migrationBuilder.DropIndex(
                name: "UX_inventory_manual_blocks_group_unit",
                schema: "inventory",
                table: "manual_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_management_operations_group_v2_shape",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_management_operations_kind",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_management_operations_result",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_inventory_units_ScopeId_PropertyId_Id",
                schema: "inventory",
                table: "inventory_units");

            migrationBuilder.DropColumn(
                name: "AvailabilitySelectionVersion",
                schema: "inventory",
                table: "property_topology");

            migrationBuilder.DropColumn(
                name: "ResultActiveBlockCount",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultAlreadyReleasedBlockCount",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultBlockGroupStatus",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultCreatedBlockCount",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultMembershipDigest",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultPreviousBlockGroupId",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultReleasedBlockCount",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.DropColumn(
                name: "ResultTotalBlockCount",
                schema: "inventory",
                table: "management_operations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_inventory_units_ScopeId_Id",
                schema: "inventory",
                table: "inventory_units",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_tenant_destroy_operation_progress",
                schema: "inventory",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 20 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_manual_blocks_ScopeId_InventoryUnitId",
                schema: "inventory",
                table: "manual_blocks",
                columns: new[] { "ScopeId", "InventoryUnitId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_management_operations_kind",
                schema: "inventory",
                table: "management_operations",
                sql: "\"Kind\" BETWEEN 1 AND 11 AND ((\"Kind\" = 1 AND \"ResourceKind\" = 1) OR (\"Kind\" IN (2, 3) AND \"ResourceKind\" = 2 AND \"ResourceId\" = \"PropertyId\") OR (\"Kind\" = 4 AND \"ResourceKind\" = 3 AND \"ResultBlockId\" IS NOT NULL AND \"ResourceId\" = \"ResultBlockId\") OR (\"Kind\" = 5 AND \"ResourceKind\" = 4 AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResourceId\" = \"ResultBlockGroupId\") OR (\"Kind\" = 6 AND \"ResourceKind\" = 5) OR (\"Kind\" = 7 AND \"ResourceKind\" = 6 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 8 AND \"ResourceKind\" = 1) OR (\"Kind\" = 9 AND \"ResourceKind\" = 7 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 10 AND \"ResourceKind\" = 6 AND \"ResourceId\" = \"ResultTopologyChangeId\") OR (\"Kind\" = 11 AND \"ResourceKind\" = 7 AND \"ResourceId\" = \"ResultTopologyChangeId\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_management_operations_result",
                schema: "inventory",
                table: "management_operations",
                sql: "(\"Kind\" = 1 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" BETWEEN \"ExpectedVersion\" AND \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NOT NULL AND \"ResultSalesMode\" IN (2, 3) AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 2 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NOT NULL AND \"ResultBlockId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NOT NULL AND \"ResultBlockStatus\" = 1 AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" = 1 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 3 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" > 0 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 4 AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NOT NULL AND \"ResultBlockId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NOT NULL AND \"ResultBlockStatus\" = 2 AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" = 1 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" = 5 AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NOT NULL AND \"ResultBlockGroupId\" <> '00000000-0000-0000-0000-000000000000' AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NOT NULL AND \"ResultAffectedBlockCount\" > 0 AND \"ResultTopologyChangeId\" IS NULL) OR (\"Kind\" IN (6, 8) AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" > 0 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NOT NULL AND \"ResultTopologyChangeId\" <> '00000000-0000-0000-0000-000000000000') OR (\"Kind\" IN (7, 9, 10, 11) AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1 AND \"ResultSalesMode\" IS NULL AND \"ResultBlockId\" IS NULL AND \"ResultBlockGroupId\" IS NULL AND \"ResultBlockStatus\" IS NULL AND \"ResultAffectedBlockCount\" IS NULL AND \"ResultTopologyChangeId\" IS NOT NULL AND \"ResultTopologyChangeId\" <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.AddForeignKey(
                name: "FK_manual_blocks_inventory_units_ScopeId_InventoryUnitId",
                schema: "inventory",
                table: "manual_blocks",
                columns: new[] { "ScopeId", "InventoryUnitId" },
                principalSchema: "inventory",
                principalTable: "inventory_units",
                principalColumns: new[] { "ScopeId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
