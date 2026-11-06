using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestRetentionRetryAttemptIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "(\"LastExecutionId\" IS NULL AND ((\"Version\" = 1 AND \"AfterProjectionOrdinal\" = 0) OR \"Version\" >= 3)) OR (\"LastExecutionId\" IS NOT NULL AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"Version\" >= 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_retention_sweep_checkpoints_lifecycle",
                schema: "guests",
                table: "guest_retention_sweep_checkpoints",
                sql: "(\"LastExecutionId\" IS NULL AND \"AfterProjectionOrdinal\" = 0 AND \"Version\" = 1) OR (\"LastExecutionId\" IS NOT NULL AND \"LastExecutionId\" <> '00000000-0000-0000-0000-000000000000' AND \"Version\" >= 2)");
        }
    }
}
