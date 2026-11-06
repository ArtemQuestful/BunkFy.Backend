using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Reservations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStationJobGrantRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "JobGrantRevision",
                schema: "reservations",
                table: "station_attributions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_station_attribution_job_grant",
                schema: "reservations",
                table: "station_attributions",
                sql: "\"JobGrantRevision\" IS NULL OR (\"Authority\" = 2 AND \"JobGrantRevision\" > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $station_job_revision$
                BEGIN
                    IF EXISTS (SELECT 1 FROM reservations.station_attributions WHERE "JobGrantRevision" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM reservations.station_attributions a
                           JOIN reservations.management_operations o ON o."ScopeId" = a."ScopeId"
                               AND o."ReservationId" = a."ReservationId" AND o."Id" = a."OperationId"
                           WHERE o."Kind" = 4) THEN
                        RAISE EXCEPTION 'Used station job attribution must be retained; disable the feature and forward-fix.';
                    END IF;
                END $station_job_revision$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_station_attribution_job_grant",
                schema: "reservations",
                table: "station_attributions");

            migrationBuilder.DropColumn(
                name: "JobGrantRevision",
                schema: "reservations",
                table: "station_attributions");
        }
    }
}
