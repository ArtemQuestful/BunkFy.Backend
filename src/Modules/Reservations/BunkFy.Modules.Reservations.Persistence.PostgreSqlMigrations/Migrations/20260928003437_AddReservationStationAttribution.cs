using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Reservations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationStationAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "station_attributions",
                schema: "reservations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrowserSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Authority = table.Column<int>(type: "integer", nullable: false),
                    ResultingVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_attributions", x => new { x.ScopeId, x.ReservationId, x.OperationId });
                    table.CheckConstraint("CK_station_attribution_authority", "\"Authority\" IN (1, 2)");
                    table.CheckConstraint("CK_station_attribution_identity", "\"ReservationId\" <> '00000000-0000-0000-0000-000000000000' AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000' AND \"StationId\" <> '00000000-0000-0000-0000-000000000000' AND \"BrowserSessionId\" <> '00000000-0000-0000-0000-000000000000' AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000' AND \"ActorSessionId\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_station_attribution_versions", "\"Generation\" > 0 AND \"ResultingVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_station_attributions_management_operations_ScopeId_Reservat~",
                        columns: x => new { x.ScopeId, x.ReservationId, x.OperationId },
                        principalSchema: "reservations",
                        principalTable: "management_operations",
                        principalColumns: new[] { "ScopeId", "ReservationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "station_attributions",
                schema: "reservations");
        }
    }
}
