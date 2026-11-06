using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Inventory.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAllocationAmendmentDecisionTenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_allocation_amendment_decisions",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_allocation_amendment_decisions",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                columns: new[] { "ScopeId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"AllocationId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND char_length(\"ScopeId\") > 0 AND \"ScopeId\" = btrim(\"ScopeId\") AND \"ScopeId\" !~ '[[:space:][:cntrl:]]'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_decided_at",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "\"DecidedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
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
                        FROM inventory.allocation_amendment_decisions
                        GROUP BY "Id"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION
                            'Cannot downgrade Inventory while amendment request ids are shared across tenants.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_allocation_amendment_decisions",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_allocation_amendment_decisions_decided_at",
                schema: "inventory",
                table: "allocation_amendment_decisions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_allocation_amendment_decisions",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                column: "Id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_allocation_amendment_decisions_coordinates",
                schema: "inventory",
                table: "allocation_amendment_decisions",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"AllocationId\" <> '00000000-0000-0000-0000-000000000000' AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND length(trim(\"ScopeId\")) > 0");
        }
    }
}
