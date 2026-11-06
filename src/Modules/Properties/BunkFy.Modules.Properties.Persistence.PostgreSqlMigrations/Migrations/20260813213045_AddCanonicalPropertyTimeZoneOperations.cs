using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BunkFy.Modules.Properties.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalPropertyTimeZoneOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE
                    properties.properties,
                    properties.rooms,
                    properties.property_governance_revisions,
                    properties.property_mutation_operations,
                    properties.property_operation_locks,
                    properties.room_operation_locks,
                    properties.tenant_revisions,
                    properties.tenant_destroy_operations,
                    properties.tenant_destroy_receipts,
                    properties.inbox_messages,
                    properties.outbox_messages
                IN ACCESS EXCLUSIVE MODE NOWAIT;

                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM properties.properties
                        WHERE char_length("TimeZoneId") = 0
                           OR btrim(
                                "TimeZoneId",
                                concat(
                                    chr(9), chr(10), chr(11), chr(12),
                                    chr(13), chr(32), chr(133), chr(160),
                                    chr(5760), chr(8192), chr(8193),
                                    chr(8194), chr(8195), chr(8196),
                                    chr(8197), chr(8198), chr(8199),
                                    chr(8200), chr(8201), chr(8202),
                                    chr(8232), chr(8233), chr(8239),
                                    chr(8287), chr(12288))) <>
                                "TimeZoneId"
                           OR EXISTS (
                                SELECT 1
                                FROM (
                                    SELECT generate_series(1, 31) AS codepoint
                                    UNION ALL
                                    SELECT generate_series(127, 159)
                                ) controls
                                WHERE strpos(
                                    "TimeZoneId",
                                    chr(controls.codepoint)) > 0))
                    THEN
                        RAISE EXCEPTION
                            'Cannot add property time-zone operations because a persisted TimeZoneId cannot be restored.'
                            USING ERRCODE = 'P0001';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM properties.tenant_destroy_operations
                        WHERE "Stage" <> 10) OR
                       EXISTS (
                        SELECT 1
                        FROM properties.tenant_revisions
                        WHERE "LifecycleStatus" = 2)
                    THEN
                        RAISE EXCEPTION
                            'Cannot add property time-zone operations while tenant destruction is in flight.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM properties.tenant_revisions
                        WHERE "Revision" = 9223372036854775807)
                    THEN
                        RAISE EXCEPTION
                            'Cannot invalidate Properties tenant revisions because a revision is exhausted.'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $migration$;

                WITH scopes AS (
                    SELECT "ScopeId" FROM properties.tenant_revisions
                    UNION SELECT "ScopeId" FROM properties.properties
                    UNION SELECT "ScopeId" FROM properties.rooms
                    UNION SELECT "ScopeId" FROM properties.property_governance_revisions
                    UNION SELECT "ScopeId" FROM properties.property_mutation_operations
                    UNION SELECT "ScopeId" FROM properties.property_operation_locks
                    UNION SELECT "ScopeId" FROM properties.room_operation_locks
                    UNION SELECT "ScopeId" FROM properties.tenant_destroy_receipts
                    UNION SELECT "ScopeId" FROM properties.inbox_messages
                    UNION SELECT "ScopeId" FROM properties.outbox_messages
                )
                INSERT INTO properties.tenant_revisions AS revisions
                    ("ScopeId", "Revision", "LifecycleStatus")
                SELECT "ScopeId", 1, 1
                FROM scopes
                WHERE "ScopeId" IS NOT NULL
                ON CONFLICT ("ScopeId") DO UPDATE
                SET "Revision" = revisions."Revision" + 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_properties_tenant_destroy_operation_progress",
                schema: "properties",
                table: "tenant_destroy_operations");

            migrationBuilder.CreateTable(
                name: "property_time_zone_catalog_entries",
                schema: "properties",
                columns: table => new
                {
                    CatalogVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_time_zone_catalog_entries", x => new { x.CatalogVersion, x.TimeZoneId });
                    table.CheckConstraint("CK_properties_property_time_zone_catalog_entry_ordinal", "\"Ordinal\" > 0");
                    table.CheckConstraint("CK_properties_property_time_zone_catalog_entry_text", "char_length(\"CatalogVersion\") > 0 AND btrim(\"CatalogVersion\") = \"CatalogVersion\" AND char_length(\"TimeZoneId\") > 0 AND btrim(\"TimeZoneId\") = \"TimeZoneId\" AND \"CatalogVersion\" !~ '[[:cntrl:]]' AND \"TimeZoneId\" !~ '[[:cntrl:]]'");
                });

            migrationBuilder.CreateTable(
                name: "property_time_zone_catalog_resolutions",
                schema: "properties",
                columns: table => new
                {
                    CatalogVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CanonicalTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_time_zone_catalog_resolutions", x => new { x.CatalogVersion, x.RequestedTimeZoneId });
                    table.UniqueConstraint("AK_property_time_zone_catalog_resolutions_CatalogVersion_Reque~", x => new { x.CatalogVersion, x.RequestedTimeZoneId, x.CanonicalTimeZoneId });
                    table.CheckConstraint("CK_properties_property_time_zone_catalog_resolution_ordinal", "\"Ordinal\" > 0");
                    table.CheckConstraint("CK_properties_property_time_zone_catalog_resolution_text", "char_length(\"CatalogVersion\") > 0 AND btrim(\"CatalogVersion\") = \"CatalogVersion\" AND char_length(\"RequestedTimeZoneId\") > 0 AND btrim(\"RequestedTimeZoneId\") = \"RequestedTimeZoneId\" AND char_length(\"CanonicalTimeZoneId\") > 0 AND btrim(\"CanonicalTimeZoneId\") = \"CanonicalTimeZoneId\" AND \"CatalogVersion\" !~ '[[:cntrl:]]' AND \"RequestedTimeZoneId\" !~ '[[:cntrl:]]' AND \"CanonicalTimeZoneId\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_property_time_zone_catalog_resolutions_property_time_zone_c~",
                        columns: x => new { x.CatalogVersion, x.CanonicalTimeZoneId },
                        principalSchema: "properties",
                        principalTable: "property_time_zone_catalog_entries",
                        principalColumns: new[] { "CatalogVersion", "TimeZoneId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "property_time_zone_operations",
                schema: "properties",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeKind = table.Column<int>(type: "integer", nullable: false),
                    RequestedTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PreviousTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CatalogVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
                    ResultVersion = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_time_zone_operations", x => new { x.ScopeId, x.PropertyId, x.OperationId });
                    table.CheckConstraint("CK_properties_property_time_zone_operation_change", "(\"ChangeKind\" = 1 AND \"OperationId\" = \"PropertyId\" AND \"PreviousTimeZoneId\" IS NULL AND \"ExpectedVersion\" = 0 AND \"ResultVersion\" = 1) OR (\"ChangeKind\" = 2 AND \"PreviousTimeZoneId\" IS NOT NULL AND \"PreviousTimeZoneId\" = \"TimeZoneId\" AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\") OR (\"ChangeKind\" IN (3, 4) AND \"PreviousTimeZoneId\" IS NOT NULL AND \"PreviousTimeZoneId\" <> \"TimeZoneId\" AND \"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1)");
                    table.CheckConstraint("CK_properties_property_time_zone_operation_ids", "\"RevisionId\" <> '00000000-0000-0000-0000-000000000000' AND \"RevisionId\" <> \"OperationId\" AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_properties_property_time_zone_operation_text", "char_length(\"ScopeId\") > 0 AND btrim(\"ScopeId\") = \"ScopeId\" AND char_length(\"RequestedTimeZoneId\") > 0 AND btrim(\"RequestedTimeZoneId\") = \"RequestedTimeZoneId\" AND (\"PreviousTimeZoneId\" IS NULL OR (char_length(\"PreviousTimeZoneId\") > 0 AND btrim(\"PreviousTimeZoneId\") = \"PreviousTimeZoneId\")) AND char_length(\"TimeZoneId\") > 0 AND btrim(\"TimeZoneId\") = \"TimeZoneId\" AND char_length(\"CatalogVersion\") > 0 AND btrim(\"CatalogVersion\") = \"CatalogVersion\" AND char_length(\"ActorId\") > 0 AND btrim(\"ActorId\") = \"ActorId\" AND \"ScopeId\" !~ '[[:cntrl:]]' AND \"RequestedTimeZoneId\" !~ '[[:cntrl:]]' AND (\"PreviousTimeZoneId\" IS NULL OR \"PreviousTimeZoneId\" !~ '[[:cntrl:]]') AND \"TimeZoneId\" !~ '[[:cntrl:]]' AND \"CatalogVersion\" !~ '[[:cntrl:]]' AND \"ActorId\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_property_time_zone_operations_properties_ScopeId_PropertyId",
                        columns: x => new { x.ScopeId, x.PropertyId },
                        principalSchema: "properties",
                        principalTable: "properties",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_time_zone_operations_property_operation_locks_Scop~",
                        columns: x => new { x.ScopeId, x.PropertyId },
                        principalSchema: "properties",
                        principalTable: "property_operation_locks",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_time_zone_operations_property_time_zone_catalog_en~",
                        columns: x => new { x.CatalogVersion, x.TimeZoneId },
                        principalSchema: "properties",
                        principalTable: "property_time_zone_catalog_entries",
                        principalColumns: new[] { "CatalogVersion", "TimeZoneId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_time_zone_operations_property_time_zone_catalog_re~",
                        columns: x => new { x.CatalogVersion, x.RequestedTimeZoneId, x.TimeZoneId },
                        principalSchema: "properties",
                        principalTable: "property_time_zone_catalog_resolutions",
                        principalColumns: new[] { "CatalogVersion", "RequestedTimeZoneId", "CanonicalTimeZoneId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "properties",
                table: "property_time_zone_catalog_entries",
                columns: new[] { "CatalogVersion", "TimeZoneId", "Ordinal" },
                values: new object[,]
                {
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Abidjan", 1 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Algiers", 2 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Bissau", 3 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Cairo", 4 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Casablanca", 5 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Ceuta", 6 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/El_Aaiun", 7 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Johannesburg", 8 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Juba", 9 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Khartoum", 10 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Lagos", 11 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Maputo", 12 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Monrovia", 13 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Nairobi", 14 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Ndjamena", 15 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Sao_Tome", 16 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Tripoli", 17 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Tunis", 18 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Windhoek", 19 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Adak", 20 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Anchorage", 21 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Araguaina", 22 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Buenos_Aires", 23 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Catamarca", 24 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Cordoba", 25 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Jujuy", 26 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/La_Rioja", 27 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Mendoza", 28 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Rio_Gallegos", 29 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Salta", 30 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/San_Juan", 31 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/San_Luis", 32 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Tucuman", 33 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Ushuaia", 34 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Asuncion", 35 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bahia", 36 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bahia_Banderas", 37 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Barbados", 38 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Belem", 39 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Belize", 40 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Boa_Vista", 41 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bogota", 42 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Boise", 43 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cambridge_Bay", 44 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Campo_Grande", 45 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cancun", 46 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Caracas", 47 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cayenne", 48 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Chicago", 49 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Chihuahua", 50 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Ciudad_Juarez", 51 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Costa_Rica", 52 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Coyhaique", 53 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cuiaba", 54 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Danmarkshavn", 55 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Dawson", 56 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Dawson_Creek", 57 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Denver", 58 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Detroit", 59 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Edmonton", 60 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Eirunepe", 61 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/El_Salvador", 62 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Fort_Nelson", 63 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Fortaleza", 64 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Glace_Bay", 65 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Goose_Bay", 66 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Grand_Turk", 67 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guatemala", 68 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guayaquil", 69 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guyana", 70 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Halifax", 71 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Havana", 72 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Hermosillo", 73 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Indianapolis", 74 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Knox", 75 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Marengo", 76 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Petersburg", 77 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Tell_City", 78 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Vevay", 79 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Vincennes", 80 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Winamac", 81 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Inuvik", 82 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Iqaluit", 83 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Jamaica", 84 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Juneau", 85 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Kentucky/Louisville", 86 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Kentucky/Monticello", 87 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/La_Paz", 88 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Lima", 89 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Los_Angeles", 90 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Maceio", 91 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Managua", 92 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Manaus", 93 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Martinique", 94 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Matamoros", 95 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Mazatlan", 96 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Menominee", 97 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Merida", 98 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Metlakatla", 99 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Mexico_City", 100 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Miquelon", 101 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Moncton", 102 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Monterrey", 103 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Montevideo", 104 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/New_York", 105 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nome", 106 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Noronha", 107 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/Beulah", 108 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/Center", 109 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/New_Salem", 110 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nuuk", 111 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Ojinaga", 112 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Panama", 113 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Paramaribo", 114 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Phoenix", 115 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Port-au-Prince", 116 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Porto_Velho", 117 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Puerto_Rico", 118 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Punta_Arenas", 119 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rankin_Inlet", 120 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Recife", 121 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Regina", 122 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Resolute", 123 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rio_Branco", 124 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santarem", 125 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santiago", 126 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santo_Domingo", 127 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Sao_Paulo", 128 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Scoresbysund", 129 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Sitka", 130 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Johns", 131 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Swift_Current", 132 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Tegucigalpa", 133 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Thule", 134 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Tijuana", 135 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Toronto", 136 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Vancouver", 137 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Whitehorse", 138 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Winnipeg", 139 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Yakutat", 140 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Casey", 141 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Davis", 142 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Macquarie", 143 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Mawson", 144 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Palmer", 145 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Rothera", 146 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Troll", 147 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Vostok", 148 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Almaty", 149 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Amman", 150 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Anadyr", 151 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Aqtau", 152 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Aqtobe", 153 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ashgabat", 154 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Atyrau", 155 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Baghdad", 156 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Baku", 157 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Bangkok", 158 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Barnaul", 159 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Beirut", 160 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Bishkek", 161 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Chita", 162 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Colombo", 163 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Damascus", 164 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dhaka", 165 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dili", 166 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dubai", 167 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dushanbe", 168 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Famagusta", 169 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Gaza", 170 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hebron", 171 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ho_Chi_Minh", 172 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hong_Kong", 173 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hovd", 174 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Irkutsk", 175 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jakarta", 176 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jayapura", 177 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jerusalem", 178 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kabul", 179 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kamchatka", 180 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Karachi", 181 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kathmandu", 182 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Khandyga", 183 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kolkata", 184 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Krasnoyarsk", 185 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kuching", 186 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Macau", 187 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Magadan", 188 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Makassar", 189 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Manila", 190 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Nicosia", 191 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Novokuznetsk", 192 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Novosibirsk", 193 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Omsk", 194 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Oral", 195 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Pontianak", 196 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Pyongyang", 197 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qatar", 198 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qostanay", 199 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qyzylorda", 200 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Riyadh", 201 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Sakhalin", 202 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Samarkand", 203 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Seoul", 204 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Shanghai", 205 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Singapore", 206 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Srednekolymsk", 207 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Taipei", 208 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tashkent", 209 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tbilisi", 210 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tehran", 211 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Thimphu", 212 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tokyo", 213 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tomsk", 214 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ulaanbaatar", 215 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Urumqi", 216 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ust-Nera", 217 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Vladivostok", 218 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yakutsk", 219 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yangon", 220 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yekaterinburg", 221 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yerevan", 222 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Azores", 223 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Bermuda", 224 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Canary", 225 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Cape_Verde", 226 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Faroe", 227 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Madeira", 228 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/South_Georgia", 229 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Stanley", 230 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Adelaide", 231 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Brisbane", 232 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Broken_Hill", 233 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Darwin", 234 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Eucla", 235 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Hobart", 236 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Lindeman", 237 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Lord_Howe", 238 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Melbourne", 239 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Perth", 240 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Sydney", 241 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT", 242 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-1", 255 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-10", 256 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-11", 257 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-12", 258 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-13", 259 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-14", 260 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-2", 261 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-3", 262 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-4", 263 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-5", 264 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-6", 265 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-7", 266 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-8", 267 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-9", 268 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+1", 243 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+10", 244 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+11", 245 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+12", 246 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+2", 247 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+3", 248 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+4", 249 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+5", 250 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+6", 251 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+7", 252 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+8", 253 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+9", 254 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/UTC", 269 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Andorra", 270 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Astrakhan", 271 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Athens", 272 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Belgrade", 273 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Berlin", 274 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Brussels", 275 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Bucharest", 276 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Budapest", 277 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Chisinau", 278 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Dublin", 279 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Gibraltar", 280 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Helsinki", 281 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Istanbul", 282 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kaliningrad", 283 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kirov", 284 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kyiv", 285 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Lisbon", 286 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/London", 287 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Madrid", 288 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Malta", 289 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Minsk", 290 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Moscow", 291 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Paris", 292 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Prague", 293 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Riga", 294 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Rome", 295 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Samara", 296 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Saratov", 297 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Simferopol", 298 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Sofia", 299 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Tallinn", 300 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Tirane", 301 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Ulyanovsk", 302 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vienna", 303 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vilnius", 304 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Volgograd", 305 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Warsaw", 306 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Zurich", 307 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Chagos", 308 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Maldives", 309 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Mauritius", 310 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Apia", 311 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Auckland", 312 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Bougainville", 313 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Chatham", 314 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Easter", 315 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Efate", 316 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Fakaofo", 317 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Fiji", 318 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Galapagos", 319 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Gambier", 320 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Guadalcanal", 321 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Guam", 322 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Honolulu", 323 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kanton", 324 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kiritimati", 325 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kosrae", 326 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kwajalein", 327 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Marquesas", 328 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Nauru", 329 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Niue", 330 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Norfolk", 331 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Noumea", 332 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Pago_Pago", 333 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Palau", 334 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Pitcairn", 335 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Port_Moresby", 336 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Rarotonga", 337 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tahiti", 338 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tarawa", 339 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tongatapu", 340 }
                });

            migrationBuilder.InsertData(
                schema: "properties",
                table: "property_time_zone_catalog_resolutions",
                columns: new[] { "CatalogVersion", "RequestedTimeZoneId", "CanonicalTimeZoneId", "Ordinal" },
                values: new object[,]
                {
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Abidjan", "Africa/Abidjan", 1 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Accra", "Africa/Abidjan", 2 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Addis_Ababa", "Africa/Nairobi", 3 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Algiers", "Africa/Algiers", 4 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Asmara", "Africa/Nairobi", 5 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Asmera", "Africa/Nairobi", 6 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Bamako", "Africa/Abidjan", 7 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Bangui", "Africa/Lagos", 8 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Banjul", "Africa/Abidjan", 9 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Bissau", "Africa/Bissau", 10 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Blantyre", "Africa/Maputo", 11 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Brazzaville", "Africa/Lagos", 12 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Bujumbura", "Africa/Maputo", 13 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Cairo", "Africa/Cairo", 14 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Casablanca", "Africa/Casablanca", 15 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Ceuta", "Africa/Ceuta", 16 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Conakry", "Africa/Abidjan", 17 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Dakar", "Africa/Abidjan", 18 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Dar_es_Salaam", "Africa/Nairobi", 19 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Djibouti", "Africa/Nairobi", 20 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Douala", "Africa/Lagos", 21 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/El_Aaiun", "Africa/El_Aaiun", 22 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Freetown", "Africa/Abidjan", 23 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Gaborone", "Africa/Maputo", 24 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Harare", "Africa/Maputo", 25 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Johannesburg", "Africa/Johannesburg", 26 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Juba", "Africa/Juba", 27 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Kampala", "Africa/Nairobi", 28 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Khartoum", "Africa/Khartoum", 29 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Kigali", "Africa/Maputo", 30 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Kinshasa", "Africa/Lagos", 31 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Lagos", "Africa/Lagos", 32 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Libreville", "Africa/Lagos", 33 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Lome", "Africa/Abidjan", 34 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Luanda", "Africa/Lagos", 35 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Lubumbashi", "Africa/Maputo", 36 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Lusaka", "Africa/Maputo", 37 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Malabo", "Africa/Lagos", 38 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Maputo", "Africa/Maputo", 39 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Maseru", "Africa/Johannesburg", 40 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Mbabane", "Africa/Johannesburg", 41 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Mogadishu", "Africa/Nairobi", 42 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Monrovia", "Africa/Monrovia", 43 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Nairobi", "Africa/Nairobi", 44 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Ndjamena", "Africa/Ndjamena", 45 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Niamey", "Africa/Lagos", 46 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Nouakchott", "Africa/Abidjan", 47 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Ouagadougou", "Africa/Abidjan", 48 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Porto-Novo", "Africa/Lagos", 49 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Sao_Tome", "Africa/Sao_Tome", 50 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Timbuktu", "Africa/Abidjan", 51 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Tripoli", "Africa/Tripoli", 52 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Tunis", "Africa/Tunis", 53 },
                    { "TZDB: 2026c (mapping: 48.2)", "Africa/Windhoek", "Africa/Windhoek", 54 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Adak", "America/Adak", 55 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Anchorage", "America/Anchorage", 56 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Anguilla", "America/Puerto_Rico", 57 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Antigua", "America/Puerto_Rico", 58 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Araguaina", "America/Araguaina", 59 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Buenos_Aires", "America/Argentina/Buenos_Aires", 60 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Catamarca", "America/Argentina/Catamarca", 61 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/ComodRivadavia", "America/Argentina/Catamarca", 62 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Cordoba", "America/Argentina/Cordoba", 63 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Jujuy", "America/Argentina/Jujuy", 64 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/La_Rioja", "America/Argentina/La_Rioja", 65 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Mendoza", "America/Argentina/Mendoza", 66 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Rio_Gallegos", "America/Argentina/Rio_Gallegos", 67 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Salta", "America/Argentina/Salta", 68 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/San_Juan", "America/Argentina/San_Juan", 69 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/San_Luis", "America/Argentina/San_Luis", 70 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Tucuman", "America/Argentina/Tucuman", 71 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Argentina/Ushuaia", "America/Argentina/Ushuaia", 72 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Aruba", "America/Puerto_Rico", 73 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Asuncion", "America/Asuncion", 74 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Atikokan", "America/Panama", 75 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Atka", "America/Adak", 76 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bahia", "America/Bahia", 77 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bahia_Banderas", "America/Bahia_Banderas", 78 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Barbados", "America/Barbados", 79 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Belem", "America/Belem", 80 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Belize", "America/Belize", 81 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Blanc-Sablon", "America/Puerto_Rico", 82 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Boa_Vista", "America/Boa_Vista", 83 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Bogota", "America/Bogota", 84 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Boise", "America/Boise", 85 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Buenos_Aires", "America/Argentina/Buenos_Aires", 86 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cambridge_Bay", "America/Cambridge_Bay", 87 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Campo_Grande", "America/Campo_Grande", 88 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cancun", "America/Cancun", 89 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Caracas", "America/Caracas", 90 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Catamarca", "America/Argentina/Catamarca", 91 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cayenne", "America/Cayenne", 92 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cayman", "America/Panama", 93 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Chicago", "America/Chicago", 94 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Chihuahua", "America/Chihuahua", 95 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Ciudad_Juarez", "America/Ciudad_Juarez", 96 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Coral_Harbour", "America/Panama", 97 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cordoba", "America/Argentina/Cordoba", 98 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Costa_Rica", "America/Costa_Rica", 99 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Coyhaique", "America/Coyhaique", 100 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Creston", "America/Phoenix", 101 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Cuiaba", "America/Cuiaba", 102 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Curacao", "America/Puerto_Rico", 103 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Danmarkshavn", "America/Danmarkshavn", 104 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Dawson", "America/Dawson", 105 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Dawson_Creek", "America/Dawson_Creek", 106 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Denver", "America/Denver", 107 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Detroit", "America/Detroit", 108 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Dominica", "America/Puerto_Rico", 109 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Edmonton", "America/Edmonton", 110 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Eirunepe", "America/Eirunepe", 111 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/El_Salvador", "America/El_Salvador", 112 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Ensenada", "America/Tijuana", 113 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Fort_Nelson", "America/Fort_Nelson", 114 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Fort_Wayne", "America/Indiana/Indianapolis", 115 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Fortaleza", "America/Fortaleza", 116 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Glace_Bay", "America/Glace_Bay", 117 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Godthab", "America/Nuuk", 118 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Goose_Bay", "America/Goose_Bay", 119 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Grand_Turk", "America/Grand_Turk", 120 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Grenada", "America/Puerto_Rico", 121 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guadeloupe", "America/Puerto_Rico", 122 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guatemala", "America/Guatemala", 123 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guayaquil", "America/Guayaquil", 124 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Guyana", "America/Guyana", 125 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Halifax", "America/Halifax", 126 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Havana", "America/Havana", 127 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Hermosillo", "America/Hermosillo", 128 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Indianapolis", "America/Indiana/Indianapolis", 129 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Knox", "America/Indiana/Knox", 130 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Marengo", "America/Indiana/Marengo", 131 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Petersburg", "America/Indiana/Petersburg", 132 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Tell_City", "America/Indiana/Tell_City", 133 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Vevay", "America/Indiana/Vevay", 134 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Vincennes", "America/Indiana/Vincennes", 135 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indiana/Winamac", "America/Indiana/Winamac", 136 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Indianapolis", "America/Indiana/Indianapolis", 137 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Inuvik", "America/Inuvik", 138 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Iqaluit", "America/Iqaluit", 139 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Jamaica", "America/Jamaica", 140 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Jujuy", "America/Argentina/Jujuy", 141 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Juneau", "America/Juneau", 142 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Kentucky/Louisville", "America/Kentucky/Louisville", 143 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Kentucky/Monticello", "America/Kentucky/Monticello", 144 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Knox_IN", "America/Indiana/Knox", 145 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Kralendijk", "America/Puerto_Rico", 146 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/La_Paz", "America/La_Paz", 147 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Lima", "America/Lima", 148 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Los_Angeles", "America/Los_Angeles", 149 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Louisville", "America/Kentucky/Louisville", 150 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Lower_Princes", "America/Puerto_Rico", 151 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Maceio", "America/Maceio", 152 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Managua", "America/Managua", 153 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Manaus", "America/Manaus", 154 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Marigot", "America/Puerto_Rico", 155 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Martinique", "America/Martinique", 156 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Matamoros", "America/Matamoros", 157 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Mazatlan", "America/Mazatlan", 158 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Mendoza", "America/Argentina/Mendoza", 159 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Menominee", "America/Menominee", 160 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Merida", "America/Merida", 161 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Metlakatla", "America/Metlakatla", 162 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Mexico_City", "America/Mexico_City", 163 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Miquelon", "America/Miquelon", 164 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Moncton", "America/Moncton", 165 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Monterrey", "America/Monterrey", 166 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Montevideo", "America/Montevideo", 167 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Montreal", "America/Toronto", 168 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Montserrat", "America/Puerto_Rico", 169 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nassau", "America/Toronto", 170 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/New_York", "America/New_York", 171 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nipigon", "America/Toronto", 172 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nome", "America/Nome", 173 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Noronha", "America/Noronha", 174 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/Beulah", "America/North_Dakota/Beulah", 175 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/Center", "America/North_Dakota/Center", 176 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/North_Dakota/New_Salem", "America/North_Dakota/New_Salem", 177 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Nuuk", "America/Nuuk", 178 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Ojinaga", "America/Ojinaga", 179 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Panama", "America/Panama", 180 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Pangnirtung", "America/Iqaluit", 181 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Paramaribo", "America/Paramaribo", 182 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Phoenix", "America/Phoenix", 183 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Port_of_Spain", "America/Puerto_Rico", 185 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Port-au-Prince", "America/Port-au-Prince", 184 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Porto_Acre", "America/Rio_Branco", 186 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Porto_Velho", "America/Porto_Velho", 187 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Puerto_Rico", "America/Puerto_Rico", 188 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Punta_Arenas", "America/Punta_Arenas", 189 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rainy_River", "America/Winnipeg", 190 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rankin_Inlet", "America/Rankin_Inlet", 191 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Recife", "America/Recife", 192 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Regina", "America/Regina", 193 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Resolute", "America/Resolute", 194 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rio_Branco", "America/Rio_Branco", 195 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Rosario", "America/Argentina/Cordoba", 196 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santa_Isabel", "America/Tijuana", 197 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santarem", "America/Santarem", 198 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santiago", "America/Santiago", 199 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Santo_Domingo", "America/Santo_Domingo", 200 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Sao_Paulo", "America/Sao_Paulo", 201 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Scoresbysund", "America/Scoresbysund", 202 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Shiprock", "America/Denver", 203 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Sitka", "America/Sitka", 204 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Barthelemy", "America/Puerto_Rico", 205 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Johns", "America/St_Johns", 206 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Kitts", "America/Puerto_Rico", 207 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Lucia", "America/Puerto_Rico", 208 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Thomas", "America/Puerto_Rico", 209 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/St_Vincent", "America/Puerto_Rico", 210 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Swift_Current", "America/Swift_Current", 211 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Tegucigalpa", "America/Tegucigalpa", 212 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Thule", "America/Thule", 213 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Thunder_Bay", "America/Toronto", 214 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Tijuana", "America/Tijuana", 215 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Toronto", "America/Toronto", 216 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Tortola", "America/Puerto_Rico", 217 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Vancouver", "America/Vancouver", 218 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Virgin", "America/Puerto_Rico", 219 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Whitehorse", "America/Whitehorse", 220 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Winnipeg", "America/Winnipeg", 221 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Yakutat", "America/Yakutat", 222 },
                    { "TZDB: 2026c (mapping: 48.2)", "America/Yellowknife", "America/Edmonton", 223 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Casey", "Antarctica/Casey", 224 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Davis", "Antarctica/Davis", 225 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/DumontDUrville", "Pacific/Port_Moresby", 226 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Macquarie", "Antarctica/Macquarie", 227 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Mawson", "Antarctica/Mawson", 228 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/McMurdo", "Pacific/Auckland", 229 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Palmer", "Antarctica/Palmer", 230 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Rothera", "Antarctica/Rothera", 231 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/South_Pole", "Pacific/Auckland", 232 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Syowa", "Asia/Riyadh", 233 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Troll", "Antarctica/Troll", 234 },
                    { "TZDB: 2026c (mapping: 48.2)", "Antarctica/Vostok", "Antarctica/Vostok", 235 },
                    { "TZDB: 2026c (mapping: 48.2)", "Arctic/Longyearbyen", "Europe/Berlin", 236 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Aden", "Asia/Riyadh", 237 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Almaty", "Asia/Almaty", 238 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Amman", "Asia/Amman", 239 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Anadyr", "Asia/Anadyr", 240 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Aqtau", "Asia/Aqtau", 241 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Aqtobe", "Asia/Aqtobe", 242 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ashgabat", "Asia/Ashgabat", 243 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ashkhabad", "Asia/Ashgabat", 244 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Atyrau", "Asia/Atyrau", 245 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Baghdad", "Asia/Baghdad", 246 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Bahrain", "Asia/Qatar", 247 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Baku", "Asia/Baku", 248 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Bangkok", "Asia/Bangkok", 249 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Barnaul", "Asia/Barnaul", 250 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Beirut", "Asia/Beirut", 251 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Bishkek", "Asia/Bishkek", 252 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Brunei", "Asia/Kuching", 253 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Calcutta", "Asia/Kolkata", 254 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Chita", "Asia/Chita", 255 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Choibalsan", "Asia/Ulaanbaatar", 256 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Chongqing", "Asia/Shanghai", 257 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Chungking", "Asia/Shanghai", 258 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Colombo", "Asia/Colombo", 259 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dacca", "Asia/Dhaka", 260 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Damascus", "Asia/Damascus", 261 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dhaka", "Asia/Dhaka", 262 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dili", "Asia/Dili", 263 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dubai", "Asia/Dubai", 264 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Dushanbe", "Asia/Dushanbe", 265 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Famagusta", "Asia/Famagusta", 266 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Gaza", "Asia/Gaza", 267 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Harbin", "Asia/Shanghai", 268 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hebron", "Asia/Hebron", 269 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ho_Chi_Minh", "Asia/Ho_Chi_Minh", 270 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hong_Kong", "Asia/Hong_Kong", 271 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Hovd", "Asia/Hovd", 272 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Irkutsk", "Asia/Irkutsk", 273 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Istanbul", "Europe/Istanbul", 274 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jakarta", "Asia/Jakarta", 275 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jayapura", "Asia/Jayapura", 276 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Jerusalem", "Asia/Jerusalem", 277 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kabul", "Asia/Kabul", 278 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kamchatka", "Asia/Kamchatka", 279 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Karachi", "Asia/Karachi", 280 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kashgar", "Asia/Urumqi", 281 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kathmandu", "Asia/Kathmandu", 282 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Katmandu", "Asia/Kathmandu", 283 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Khandyga", "Asia/Khandyga", 284 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kolkata", "Asia/Kolkata", 285 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Krasnoyarsk", "Asia/Krasnoyarsk", 286 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kuala_Lumpur", "Asia/Singapore", 287 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kuching", "Asia/Kuching", 288 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Kuwait", "Asia/Riyadh", 289 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Macao", "Asia/Macau", 290 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Macau", "Asia/Macau", 291 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Magadan", "Asia/Magadan", 292 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Makassar", "Asia/Makassar", 293 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Manila", "Asia/Manila", 294 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Muscat", "Asia/Dubai", 295 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Nicosia", "Asia/Nicosia", 296 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Novokuznetsk", "Asia/Novokuznetsk", 297 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Novosibirsk", "Asia/Novosibirsk", 298 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Omsk", "Asia/Omsk", 299 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Oral", "Asia/Oral", 300 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Phnom_Penh", "Asia/Bangkok", 301 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Pontianak", "Asia/Pontianak", 302 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Pyongyang", "Asia/Pyongyang", 303 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qatar", "Asia/Qatar", 304 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qostanay", "Asia/Qostanay", 305 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Qyzylorda", "Asia/Qyzylorda", 306 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Rangoon", "Asia/Yangon", 307 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Riyadh", "Asia/Riyadh", 308 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Saigon", "Asia/Ho_Chi_Minh", 309 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Sakhalin", "Asia/Sakhalin", 310 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Samarkand", "Asia/Samarkand", 311 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Seoul", "Asia/Seoul", 312 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Shanghai", "Asia/Shanghai", 313 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Singapore", "Asia/Singapore", 314 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Srednekolymsk", "Asia/Srednekolymsk", 315 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Taipei", "Asia/Taipei", 316 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tashkent", "Asia/Tashkent", 317 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tbilisi", "Asia/Tbilisi", 318 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tehran", "Asia/Tehran", 319 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tel_Aviv", "Asia/Jerusalem", 320 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Thimbu", "Asia/Thimphu", 321 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Thimphu", "Asia/Thimphu", 322 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tokyo", "Asia/Tokyo", 323 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Tomsk", "Asia/Tomsk", 324 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ujung_Pandang", "Asia/Makassar", 325 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ulaanbaatar", "Asia/Ulaanbaatar", 326 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ulan_Bator", "Asia/Ulaanbaatar", 327 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Urumqi", "Asia/Urumqi", 328 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Ust-Nera", "Asia/Ust-Nera", 329 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Vientiane", "Asia/Bangkok", 330 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Vladivostok", "Asia/Vladivostok", 331 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yakutsk", "Asia/Yakutsk", 332 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yangon", "Asia/Yangon", 333 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yekaterinburg", "Asia/Yekaterinburg", 334 },
                    { "TZDB: 2026c (mapping: 48.2)", "Asia/Yerevan", "Asia/Yerevan", 335 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Azores", "Atlantic/Azores", 336 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Bermuda", "Atlantic/Bermuda", 337 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Canary", "Atlantic/Canary", 338 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Cape_Verde", "Atlantic/Cape_Verde", 339 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Faeroe", "Atlantic/Faroe", 340 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Faroe", "Atlantic/Faroe", 341 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Jan_Mayen", "Europe/Berlin", 342 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Madeira", "Atlantic/Madeira", 343 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Reykjavik", "Africa/Abidjan", 344 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/South_Georgia", "Atlantic/South_Georgia", 345 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/St_Helena", "Africa/Abidjan", 346 },
                    { "TZDB: 2026c (mapping: 48.2)", "Atlantic/Stanley", "Atlantic/Stanley", 347 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/ACT", "Australia/Sydney", 348 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Adelaide", "Australia/Adelaide", 349 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Brisbane", "Australia/Brisbane", 350 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Broken_Hill", "Australia/Broken_Hill", 351 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Canberra", "Australia/Sydney", 352 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Currie", "Australia/Hobart", 353 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Darwin", "Australia/Darwin", 354 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Eucla", "Australia/Eucla", 355 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Hobart", "Australia/Hobart", 356 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/LHI", "Australia/Lord_Howe", 357 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Lindeman", "Australia/Lindeman", 358 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Lord_Howe", "Australia/Lord_Howe", 359 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Melbourne", "Australia/Melbourne", 360 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/North", "Australia/Darwin", 362 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/NSW", "Australia/Sydney", 361 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Perth", "Australia/Perth", 363 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Queensland", "Australia/Brisbane", 364 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/South", "Australia/Adelaide", 365 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Sydney", "Australia/Sydney", 366 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Tasmania", "Australia/Hobart", 367 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Victoria", "Australia/Melbourne", 368 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/West", "Australia/Perth", 369 },
                    { "TZDB: 2026c (mapping: 48.2)", "Australia/Yancowinna", "Australia/Broken_Hill", 370 },
                    { "TZDB: 2026c (mapping: 48.2)", "Brazil/Acre", "America/Rio_Branco", 371 },
                    { "TZDB: 2026c (mapping: 48.2)", "Brazil/DeNoronha", "America/Noronha", 372 },
                    { "TZDB: 2026c (mapping: 48.2)", "Brazil/East", "America/Sao_Paulo", 373 },
                    { "TZDB: 2026c (mapping: 48.2)", "Brazil/West", "America/Manaus", 374 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Atlantic", "America/Halifax", 377 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Central", "America/Winnipeg", 378 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Eastern", "America/Toronto", 379 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Mountain", "America/Edmonton", 380 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Newfoundland", "America/St_Johns", 381 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Pacific", "America/Vancouver", 382 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Saskatchewan", "America/Regina", 383 },
                    { "TZDB: 2026c (mapping: 48.2)", "Canada/Yukon", "America/Whitehorse", 384 },
                    { "TZDB: 2026c (mapping: 48.2)", "CET", "Europe/Brussels", 375 },
                    { "TZDB: 2026c (mapping: 48.2)", "Chile/Continental", "America/Santiago", 385 },
                    { "TZDB: 2026c (mapping: 48.2)", "Chile/EasterIsland", "Pacific/Easter", 386 },
                    { "TZDB: 2026c (mapping: 48.2)", "CST6CDT", "America/Chicago", 376 },
                    { "TZDB: 2026c (mapping: 48.2)", "Cuba", "America/Havana", 387 },
                    { "TZDB: 2026c (mapping: 48.2)", "EET", "Europe/Athens", 388 },
                    { "TZDB: 2026c (mapping: 48.2)", "Egypt", "Africa/Cairo", 391 },
                    { "TZDB: 2026c (mapping: 48.2)", "Eire", "Europe/Dublin", 392 },
                    { "TZDB: 2026c (mapping: 48.2)", "EST", "America/Panama", 389 },
                    { "TZDB: 2026c (mapping: 48.2)", "EST5EDT", "America/New_York", 390 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT", "Etc/GMT", 393 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-0", "Etc/GMT", 407 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-1", "Etc/GMT-1", 408 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-10", "Etc/GMT-10", 409 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-11", "Etc/GMT-11", 410 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-12", "Etc/GMT-12", 411 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-13", "Etc/GMT-13", 412 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-14", "Etc/GMT-14", 413 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-2", "Etc/GMT-2", 414 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-3", "Etc/GMT-3", 415 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-4", "Etc/GMT-4", 416 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-5", "Etc/GMT-5", 417 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-6", "Etc/GMT-6", 418 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-7", "Etc/GMT-7", 419 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-8", "Etc/GMT-8", 420 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT-9", "Etc/GMT-9", 421 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+0", "Etc/GMT", 394 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+1", "Etc/GMT+1", 395 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+10", "Etc/GMT+10", 396 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+11", "Etc/GMT+11", 397 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+12", "Etc/GMT+12", 398 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+2", "Etc/GMT+2", 399 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+3", "Etc/GMT+3", 400 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+4", "Etc/GMT+4", 401 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+5", "Etc/GMT+5", 402 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+6", "Etc/GMT+6", 403 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+7", "Etc/GMT+7", 404 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+8", "Etc/GMT+8", 405 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT+9", "Etc/GMT+9", 406 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/GMT0", "Etc/GMT", 422 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/Greenwich", "Etc/GMT", 423 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/UCT", "Etc/UTC", 424 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/Universal", "Etc/UTC", 426 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/UTC", "Etc/UTC", 425 },
                    { "TZDB: 2026c (mapping: 48.2)", "Etc/Zulu", "Etc/UTC", 427 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Amsterdam", "Europe/Brussels", 428 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Andorra", "Europe/Andorra", 429 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Astrakhan", "Europe/Astrakhan", 430 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Athens", "Europe/Athens", 431 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Belfast", "Europe/London", 432 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Belgrade", "Europe/Belgrade", 433 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Berlin", "Europe/Berlin", 434 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Bratislava", "Europe/Prague", 435 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Brussels", "Europe/Brussels", 436 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Bucharest", "Europe/Bucharest", 437 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Budapest", "Europe/Budapest", 438 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Busingen", "Europe/Zurich", 439 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Chisinau", "Europe/Chisinau", 440 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Copenhagen", "Europe/Berlin", 441 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Dublin", "Europe/Dublin", 442 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Gibraltar", "Europe/Gibraltar", 443 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Guernsey", "Europe/London", 444 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Helsinki", "Europe/Helsinki", 445 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Isle_of_Man", "Europe/London", 446 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Istanbul", "Europe/Istanbul", 447 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Jersey", "Europe/London", 448 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kaliningrad", "Europe/Kaliningrad", 449 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kiev", "Europe/Kyiv", 450 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kirov", "Europe/Kirov", 451 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Kyiv", "Europe/Kyiv", 452 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Lisbon", "Europe/Lisbon", 453 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Ljubljana", "Europe/Belgrade", 454 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/London", "Europe/London", 455 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Luxembourg", "Europe/Brussels", 456 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Madrid", "Europe/Madrid", 457 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Malta", "Europe/Malta", 458 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Mariehamn", "Europe/Helsinki", 459 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Minsk", "Europe/Minsk", 460 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Monaco", "Europe/Paris", 461 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Moscow", "Europe/Moscow", 462 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Nicosia", "Asia/Nicosia", 463 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Oslo", "Europe/Berlin", 464 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Paris", "Europe/Paris", 465 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Podgorica", "Europe/Belgrade", 466 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Prague", "Europe/Prague", 467 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Riga", "Europe/Riga", 468 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Rome", "Europe/Rome", 469 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Samara", "Europe/Samara", 470 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/San_Marino", "Europe/Rome", 471 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Sarajevo", "Europe/Belgrade", 472 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Saratov", "Europe/Saratov", 473 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Simferopol", "Europe/Simferopol", 474 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Skopje", "Europe/Belgrade", 475 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Sofia", "Europe/Sofia", 476 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Stockholm", "Europe/Berlin", 477 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Tallinn", "Europe/Tallinn", 478 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Tirane", "Europe/Tirane", 479 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Tiraspol", "Europe/Chisinau", 480 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Ulyanovsk", "Europe/Ulyanovsk", 481 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Uzhgorod", "Europe/Kyiv", 482 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vaduz", "Europe/Zurich", 483 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vatican", "Europe/Rome", 484 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vienna", "Europe/Vienna", 485 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Vilnius", "Europe/Vilnius", 486 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Volgograd", "Europe/Volgograd", 487 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Warsaw", "Europe/Warsaw", 488 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Zagreb", "Europe/Belgrade", 489 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Zaporozhye", "Europe/Kyiv", 490 },
                    { "TZDB: 2026c (mapping: 48.2)", "Europe/Zurich", "Europe/Zurich", 491 },
                    { "TZDB: 2026c (mapping: 48.2)", "GB", "Europe/London", 492 },
                    { "TZDB: 2026c (mapping: 48.2)", "GB-Eire", "Europe/London", 493 },
                    { "TZDB: 2026c (mapping: 48.2)", "GMT", "Etc/GMT", 494 },
                    { "TZDB: 2026c (mapping: 48.2)", "GMT-0", "Etc/GMT", 496 },
                    { "TZDB: 2026c (mapping: 48.2)", "GMT+0", "Etc/GMT", 495 },
                    { "TZDB: 2026c (mapping: 48.2)", "GMT0", "Etc/GMT", 497 },
                    { "TZDB: 2026c (mapping: 48.2)", "Greenwich", "Etc/GMT", 498 },
                    { "TZDB: 2026c (mapping: 48.2)", "Hongkong", "Asia/Hong_Kong", 500 },
                    { "TZDB: 2026c (mapping: 48.2)", "HST", "Pacific/Honolulu", 499 },
                    { "TZDB: 2026c (mapping: 48.2)", "Iceland", "Africa/Abidjan", 501 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Antananarivo", "Africa/Nairobi", 502 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Chagos", "Indian/Chagos", 503 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Christmas", "Asia/Bangkok", 504 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Cocos", "Asia/Yangon", 505 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Comoro", "Africa/Nairobi", 506 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Kerguelen", "Indian/Maldives", 507 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Mahe", "Asia/Dubai", 508 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Maldives", "Indian/Maldives", 509 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Mauritius", "Indian/Mauritius", 510 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Mayotte", "Africa/Nairobi", 511 },
                    { "TZDB: 2026c (mapping: 48.2)", "Indian/Reunion", "Asia/Dubai", 512 },
                    { "TZDB: 2026c (mapping: 48.2)", "Iran", "Asia/Tehran", 513 },
                    { "TZDB: 2026c (mapping: 48.2)", "Israel", "Asia/Jerusalem", 514 },
                    { "TZDB: 2026c (mapping: 48.2)", "Jamaica", "America/Jamaica", 515 },
                    { "TZDB: 2026c (mapping: 48.2)", "Japan", "Asia/Tokyo", 516 },
                    { "TZDB: 2026c (mapping: 48.2)", "Kwajalein", "Pacific/Kwajalein", 517 },
                    { "TZDB: 2026c (mapping: 48.2)", "Libya", "Africa/Tripoli", 518 },
                    { "TZDB: 2026c (mapping: 48.2)", "MET", "Europe/Brussels", 519 },
                    { "TZDB: 2026c (mapping: 48.2)", "Mexico/BajaNorte", "America/Tijuana", 522 },
                    { "TZDB: 2026c (mapping: 48.2)", "Mexico/BajaSur", "America/Mazatlan", 523 },
                    { "TZDB: 2026c (mapping: 48.2)", "Mexico/General", "America/Mexico_City", 524 },
                    { "TZDB: 2026c (mapping: 48.2)", "MST", "America/Phoenix", 520 },
                    { "TZDB: 2026c (mapping: 48.2)", "MST7MDT", "America/Denver", 521 },
                    { "TZDB: 2026c (mapping: 48.2)", "Navajo", "America/Denver", 527 },
                    { "TZDB: 2026c (mapping: 48.2)", "NZ", "Pacific/Auckland", 525 },
                    { "TZDB: 2026c (mapping: 48.2)", "NZ-CHAT", "Pacific/Chatham", 526 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Apia", "Pacific/Apia", 530 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Auckland", "Pacific/Auckland", 531 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Bougainville", "Pacific/Bougainville", 532 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Chatham", "Pacific/Chatham", 533 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Chuuk", "Pacific/Port_Moresby", 534 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Easter", "Pacific/Easter", 535 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Efate", "Pacific/Efate", 536 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Enderbury", "Pacific/Kanton", 537 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Fakaofo", "Pacific/Fakaofo", 538 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Fiji", "Pacific/Fiji", 539 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Funafuti", "Pacific/Tarawa", 540 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Galapagos", "Pacific/Galapagos", 541 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Gambier", "Pacific/Gambier", 542 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Guadalcanal", "Pacific/Guadalcanal", 543 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Guam", "Pacific/Guam", 544 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Honolulu", "Pacific/Honolulu", 545 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Johnston", "Pacific/Honolulu", 546 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kanton", "Pacific/Kanton", 547 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kiritimati", "Pacific/Kiritimati", 548 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kosrae", "Pacific/Kosrae", 549 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Kwajalein", "Pacific/Kwajalein", 550 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Majuro", "Pacific/Tarawa", 551 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Marquesas", "Pacific/Marquesas", 552 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Midway", "Pacific/Pago_Pago", 553 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Nauru", "Pacific/Nauru", 554 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Niue", "Pacific/Niue", 555 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Norfolk", "Pacific/Norfolk", 556 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Noumea", "Pacific/Noumea", 557 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Pago_Pago", "Pacific/Pago_Pago", 558 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Palau", "Pacific/Palau", 559 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Pitcairn", "Pacific/Pitcairn", 560 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Pohnpei", "Pacific/Guadalcanal", 561 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Ponape", "Pacific/Guadalcanal", 562 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Port_Moresby", "Pacific/Port_Moresby", 563 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Rarotonga", "Pacific/Rarotonga", 564 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Saipan", "Pacific/Guam", 565 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Samoa", "Pacific/Pago_Pago", 566 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tahiti", "Pacific/Tahiti", 567 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tarawa", "Pacific/Tarawa", 568 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Tongatapu", "Pacific/Tongatapu", 569 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Truk", "Pacific/Port_Moresby", 570 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Wake", "Pacific/Tarawa", 571 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Wallis", "Pacific/Tarawa", 572 },
                    { "TZDB: 2026c (mapping: 48.2)", "Pacific/Yap", "Pacific/Port_Moresby", 573 },
                    { "TZDB: 2026c (mapping: 48.2)", "Poland", "Europe/Warsaw", 574 },
                    { "TZDB: 2026c (mapping: 48.2)", "Portugal", "Europe/Lisbon", 575 },
                    { "TZDB: 2026c (mapping: 48.2)", "PRC", "Asia/Shanghai", 528 },
                    { "TZDB: 2026c (mapping: 48.2)", "PST8PDT", "America/Los_Angeles", 529 },
                    { "TZDB: 2026c (mapping: 48.2)", "ROC", "Asia/Taipei", 576 },
                    { "TZDB: 2026c (mapping: 48.2)", "ROK", "Asia/Seoul", 577 },
                    { "TZDB: 2026c (mapping: 48.2)", "Singapore", "Asia/Singapore", 578 },
                    { "TZDB: 2026c (mapping: 48.2)", "Turkey", "Europe/Istanbul", 579 },
                    { "TZDB: 2026c (mapping: 48.2)", "UCT", "Etc/UTC", 580 },
                    { "TZDB: 2026c (mapping: 48.2)", "Universal", "Etc/UTC", 594 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Alaska", "America/Anchorage", 581 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Aleutian", "America/Adak", 582 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Arizona", "America/Phoenix", 583 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Central", "America/Chicago", 584 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/East-Indiana", "America/Indiana/Indianapolis", 585 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Eastern", "America/New_York", 586 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Hawaii", "Pacific/Honolulu", 587 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Indiana-Starke", "America/Indiana/Knox", 588 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Michigan", "America/Detroit", 589 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Mountain", "America/Denver", 590 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Pacific", "America/Los_Angeles", 591 },
                    { "TZDB: 2026c (mapping: 48.2)", "US/Samoa", "Pacific/Pago_Pago", 592 },
                    { "TZDB: 2026c (mapping: 48.2)", "UTC", "Etc/UTC", 593 },
                    { "TZDB: 2026c (mapping: 48.2)", "W-SU", "Europe/Moscow", 595 },
                    { "TZDB: 2026c (mapping: 48.2)", "WET", "Europe/Lisbon", 596 },
                    { "TZDB: 2026c (mapping: 48.2)", "Zulu", "Etc/UTC", 597 }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_properties_tenant_destroy_operation_progress",
                schema: "properties",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 12 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_properties_scope_projection_ordinal_id",
                schema: "properties",
                table: "properties",
                columns: new[] { "ScopeId", "ProjectionOrdinal", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_catalog_entries_version_ordinal",
                schema: "properties",
                table: "property_time_zone_catalog_entries",
                columns: new[] { "CatalogVersion", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_catalog_resolutions_CatalogVersion_Canon~",
                schema: "properties",
                table: "property_time_zone_catalog_resolutions",
                columns: new[] { "CatalogVersion", "CanonicalTimeZoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_catalog_resolutions_version_ordinal",
                schema: "properties",
                table: "property_time_zone_catalog_resolutions",
                columns: new[] { "CatalogVersion", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_operations_catalog_time_zone",
                schema: "properties",
                table: "property_time_zone_operations",
                columns: new[] { "CatalogVersion", "TimeZoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_operations_CatalogVersion_RequestedTimeZ~",
                schema: "properties",
                table: "property_time_zone_operations",
                columns: new[] { "CatalogVersion", "RequestedTimeZoneId", "TimeZoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_operations_scope_occurred",
                schema: "properties",
                table: "property_time_zone_operations",
                columns: new[] { "ScopeId", "OccurredAtUtc", "PropertyId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_property_time_zone_operations_scope_revision",
                schema: "properties",
                table: "property_time_zone_operations",
                columns: new[] { "ScopeId", "RevisionId" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TABLE
                    properties.property_time_zone_transaction_proofs
                (
                    "TransactionId" xid8 NOT NULL,
                    "ScopeId" character varying(128) NOT NULL,
                    "PropertyId" uuid NOT NULL,
                    "PreviousTimeZoneId" character varying(128) NULL,
                    "TimeZoneId" character varying(128) NOT NULL,
                    "ExpectedVersion" bigint NOT NULL,
                    "ResultVersion" bigint NOT NULL,
                    "OccurredAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT
                        "PK_property_time_zone_transaction_proofs"
                        PRIMARY KEY
                        (
                            "TransactionId",
                            "ScopeId",
                            "PropertyId",
                            "ExpectedVersion",
                            "ResultVersion"
                        ),
                    CONSTRAINT
                        "CK_property_time_zone_transaction_proofs_coordinate"
                        CHECK
                        (
                            (
                                "PreviousTimeZoneId" IS NULL AND
                                "ExpectedVersion" = 0 AND
                                "ResultVersion" = 1
                            ) OR
                            (
                                "PreviousTimeZoneId" IS NOT NULL AND
                                "PreviousTimeZoneId" <>
                                    "TimeZoneId" AND
                                "ExpectedVersion" > 0 AND
                                "ResultVersion" =
                                    "ExpectedVersion" + 1
                            )
                        ),
                    CONSTRAINT
                        "CK_property_time_zone_transaction_proofs_text"
                        CHECK
                        (
                            char_length("ScopeId") > 0 AND
                            btrim("ScopeId") = "ScopeId" AND
                            (
                                "PreviousTimeZoneId" IS NULL OR
                                (
                                    char_length(
                                        "PreviousTimeZoneId") > 0 AND
                                    btrim("PreviousTimeZoneId") =
                                        "PreviousTimeZoneId" AND
                                    "PreviousTimeZoneId" !~
                                        '[[:cntrl:]]'
                                )
                            ) AND
                            char_length("TimeZoneId") > 0 AND
                            btrim("TimeZoneId") = "TimeZoneId" AND
                            "ScopeId" !~ '[[:cntrl:]]' AND
                            "TimeZoneId" !~ '[[:cntrl:]]'
                        )
                );

                CREATE FUNCTION
                    properties.guard_property_time_zone_transaction_proof()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' OR pg_trigger_depth() < 2 THEN
                        RAISE EXCEPTION
                            'property time-zone transaction proofs are trigger-owned'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END;
                $function$;

                CREATE TRIGGER
                    property_time_zone_transaction_proofs_trigger_owned
                BEFORE INSERT OR UPDATE OR DELETE
                ON properties.property_time_zone_transaction_proofs
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.guard_property_time_zone_transaction_proof();

                CREATE FUNCTION
                    properties.record_property_time_zone_transaction_proof()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' OR
                       OLD."TimeZoneId" IS DISTINCT FROM NEW."TimeZoneId"
                    THEN
                        IF TG_OP = 'INSERT' AND
                           (
                               NEW."Version" <> 1 OR
                               NEW."UpdatedAtUtc" IS NOT NULL
                           )
                        THEN
                            RAISE EXCEPTION
                                'new property time-zone proof has an invalid version coordinate'
                                USING ERRCODE = '55000';
                        END IF;

                        IF TG_OP = 'UPDATE' AND
                           (
                               NEW."Version" <> OLD."Version" + 1 OR
                               NEW."UpdatedAtUtc" IS NULL
                           )
                        THEN
                            RAISE EXCEPTION
                                'property time-zone proof has an invalid transition coordinate'
                                USING ERRCODE = '55000';
                        END IF;

                        INSERT INTO
                            properties.property_time_zone_transaction_proofs
                        (
                            "TransactionId",
                            "ScopeId",
                            "PropertyId",
                            "PreviousTimeZoneId",
                            "TimeZoneId",
                            "ExpectedVersion",
                            "ResultVersion",
                            "OccurredAtUtc"
                        )
                        VALUES
                        (
                            pg_current_xact_id(),
                            NEW."ScopeId",
                            NEW."Id",
                            CASE WHEN TG_OP = 'INSERT'
                                THEN NULL
                                ELSE OLD."TimeZoneId"
                            END,
                            NEW."TimeZoneId",
                            CASE WHEN TG_OP = 'INSERT'
                                THEN 0
                                ELSE OLD."Version"
                            END,
                            NEW."Version",
                            CASE WHEN TG_OP = 'INSERT'
                                THEN NEW."CreatedAtUtc"
                                ELSE NEW."UpdatedAtUtc"
                            END
                        );
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER properties_record_time_zone_transaction_proof
                AFTER INSERT OR UPDATE OF "TimeZoneId"
                ON properties.properties
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.record_property_time_zone_transaction_proof();

                CREATE FUNCTION
                    properties.validate_property_time_zone_operation_insert()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    property_time_zone_id text;
                    property_version bigint;
                    property_created_at timestamp with time zone;
                    property_updated_at timestamp with time zone;
                    property_same_transaction boolean;
                    lock_revision bigint;
                    lock_same_transaction boolean;
                    previous_resolves_to_result boolean;
                BEGIN
                    SELECT
                        property."TimeZoneId",
                        property."Version",
                        property."CreatedAtUtc",
                        property."UpdatedAtUtc",
                        pg_xact_status(property.xmin::text::xid8) =
                            'in progress'
                    INTO
                        property_time_zone_id,
                        property_version,
                        property_created_at,
                        property_updated_at,
                        property_same_transaction
                    FROM properties.properties property
                    WHERE property."ScopeId" = NEW."ScopeId"
                      AND property."Id" = NEW."PropertyId";

                    IF NOT FOUND THEN
                        RAISE EXCEPTION
                            'property time-zone operation has no property coordinate'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT
                        resource_lock."Revision",
                        pg_xact_status(resource_lock.xmin::text::xid8) =
                            'in progress'
                    INTO lock_revision, lock_same_transaction
                    FROM properties.property_operation_locks resource_lock
                    WHERE resource_lock."ScopeId" = NEW."ScopeId"
                      AND resource_lock."Id" = NEW."PropertyId";

                    IF NOT FOUND OR NOT lock_same_transaction THEN
                        RAISE EXCEPTION
                            'property time-zone operation lacks a same-transaction property lock'
                            USING ERRCODE = '55000';
                    END IF;

                    previous_resolves_to_result := NEW."PreviousTimeZoneId" IS NOT NULL AND
                        EXISTS (
                            SELECT 1
                            FROM properties.property_time_zone_catalog_resolutions resolution
                            WHERE resolution."CatalogVersion" = NEW."CatalogVersion"
                              AND resolution."RequestedTimeZoneId" =
                                  NEW."PreviousTimeZoneId"
                              AND resolution."CanonicalTimeZoneId" =
                                  NEW."TimeZoneId");

                    IF NEW."ChangeKind" = 1 THEN
                        DELETE FROM
                            properties.property_time_zone_transaction_proofs proof
                        WHERE proof."TransactionId" =
                                  pg_current_xact_id()
                          AND proof."ScopeId" = NEW."ScopeId"
                          AND proof."PropertyId" = NEW."PropertyId"
                          AND proof."PreviousTimeZoneId" IS NULL
                          AND proof."TimeZoneId" = NEW."TimeZoneId"
                          AND proof."ExpectedVersion" = 0
                          AND proof."ResultVersion" = NEW."ResultVersion"
                          AND proof."OccurredAtUtc" = NEW."OccurredAtUtc";

                        IF NOT FOUND THEN
                            RAISE EXCEPTION
                                'created property time-zone operation lacks its same-transaction property insertion'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NOT property_same_transaction OR
                           lock_revision <> 1 OR
                           property_version <> 1 OR
                           property_time_zone_id <> NEW."TimeZoneId" OR
                           property_created_at <> NEW."OccurredAtUtc" OR
                           property_updated_at IS NOT NULL
                        THEN
                            RAISE EXCEPTION
                                'created property time-zone operation does not match its same-transaction property'
                                USING ERRCODE = '55000';
                        END IF;
                    ELSIF NEW."ChangeKind" = 2 THEN
                        IF property_version <> NEW."ResultVersion" OR
                           property_time_zone_id <> NEW."TimeZoneId"
                        THEN
                            RAISE EXCEPTION
                                'unchanged property time-zone operation does not match current property state'
                                USING ERRCODE = '55000';
                        END IF;
                    ELSIF NEW."ChangeKind" IN (3, 4) THEN
                        DELETE FROM
                            properties.property_time_zone_transaction_proofs proof
                        WHERE proof."TransactionId" =
                                  pg_current_xact_id()
                          AND proof."ScopeId" = NEW."ScopeId"
                          AND proof."PropertyId" = NEW."PropertyId"
                          AND proof."PreviousTimeZoneId" =
                              NEW."PreviousTimeZoneId"
                          AND proof."TimeZoneId" = NEW."TimeZoneId"
                          AND proof."ExpectedVersion" =
                              NEW."ExpectedVersion"
                          AND proof."ResultVersion" = NEW."ResultVersion"
                          AND proof."OccurredAtUtc" = NEW."OccurredAtUtc";

                        IF NOT FOUND THEN
                            RAISE EXCEPTION
                                'changed property time-zone operation lacks its same-transaction time-zone transition'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NOT property_same_transaction OR
                           property_version <> NEW."ResultVersion" OR
                           property_time_zone_id <> NEW."TimeZoneId" OR
                           property_updated_at IS DISTINCT FROM
                               NEW."OccurredAtUtc"
                        THEN
                            RAISE EXCEPTION
                                'changed property time-zone operation does not match its same-transaction property'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NEW."ChangeKind" = 3 AND
                           NOT previous_resolves_to_result
                        THEN
                            RAISE EXCEPTION
                                'canonicalized property time-zone operation does not match the catalog resolution'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NEW."ChangeKind" = 4 AND
                           previous_resolves_to_result
                        THEN
                            RAISE EXCEPTION
                                'changed property time-zone operation must not be a catalog canonicalization'
                                USING ERRCODE = '55000';
                        END IF;
                    ELSE
                        RAISE EXCEPTION
                            'property time-zone operation change kind is invalid'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    property_time_zone_operations_insert_provenance
                AFTER INSERT
                ON properties.property_time_zone_operations
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.validate_property_time_zone_operation_insert();

                CREATE FUNCTION
                    properties.require_property_time_zone_operation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' AND NOT EXISTS (
                        SELECT 1
                        FROM properties.property_time_zone_operations operation
                        WHERE operation."ScopeId" = NEW."ScopeId"
                          AND operation."PropertyId" = NEW."Id"
                          AND operation."ChangeKind" = 1
                          AND operation."OperationId" = NEW."Id"
                          AND operation."PreviousTimeZoneId" IS NULL
                          AND operation."TimeZoneId" = NEW."TimeZoneId"
                          AND operation."ExpectedVersion" = 0
                          AND operation."ResultVersion" = NEW."Version"
                          AND operation."OccurredAtUtc" = NEW."CreatedAtUtc"
                          AND pg_xact_status(
                                  operation.xmin::text::xid8) =
                              'in progress')
                    THEN
                        RAISE EXCEPTION
                            'a new property requires a same-transaction created time-zone operation'
                            USING ERRCODE = '55000';
                    END IF;

                    IF TG_OP = 'UPDATE' AND
                       OLD."TimeZoneId" IS DISTINCT FROM NEW."TimeZoneId" AND
                       NOT EXISTS (
                        SELECT 1
                        FROM properties.property_time_zone_operations operation
                        WHERE operation."ScopeId" = NEW."ScopeId"
                          AND operation."PropertyId" = NEW."Id"
                          AND operation."ChangeKind" IN (3, 4)
                          AND operation."PreviousTimeZoneId" =
                              OLD."TimeZoneId"
                          AND operation."TimeZoneId" = NEW."TimeZoneId"
                          AND operation."ExpectedVersion" = OLD."Version"
                          AND operation."ResultVersion" = NEW."Version"
                          AND operation."OccurredAtUtc" = NEW."UpdatedAtUtc"
                          AND pg_xact_status(
                                  operation.xmin::text::xid8) =
                              'in progress')
                    THEN
                        RAISE EXCEPTION
                            'a property time-zone change requires a same-transaction operation'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER
                    properties_require_time_zone_operation
                AFTER INSERT OR UPDATE OF "TimeZoneId"
                ON properties.properties
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.require_property_time_zone_operation();

                CREATE FUNCTION
                    properties.reject_property_time_zone_operation_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    destroy_operation_id text;
                BEGIN
                    destroy_operation_id := current_setting(
                        'bunkfy.properties_tenant_destroy_operation_id',
                        true);
                    IF TG_OP = 'DELETE' AND
                       destroy_operation_id IS NOT NULL AND
                       EXISTS (
                           SELECT 1
                           FROM properties.tenant_destroy_operations operation
                           INNER JOIN properties.tenant_revisions state
                               ON state."ScopeId" = operation."ScopeId"
                           WHERE operation."OperationId"::text =
                                     destroy_operation_id
                             AND operation."ScopeId" = OLD."ScopeId"
                             AND operation."Stage" = 12
                             AND state."LifecycleStatus" = 2
                             AND state."DestroyOperationId" =
                                     operation."OperationId"
                             AND state."DestroyRequestSha256" =
                                     operation."RequestSha256")
                    THEN
                        RETURN OLD;
                    END IF;

                    RAISE EXCEPTION
                        'property time-zone operations are append-only'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER property_time_zone_operations_append_only
                BEFORE UPDATE OR DELETE
                ON properties.property_time_zone_operations
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.reject_property_time_zone_operation_mutation();

                CREATE FUNCTION
                    properties.reject_property_time_zone_catalog_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION
                        'property time-zone catalog rows are migration-owned and immutable'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER property_time_zone_catalog_entries_immutable
                BEFORE INSERT OR UPDATE OR DELETE
                ON properties.property_time_zone_catalog_entries
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.reject_property_time_zone_catalog_mutation();

                CREATE TRIGGER property_time_zone_catalog_resolutions_immutable
                BEFORE INSERT OR UPDATE OR DELETE
                ON properties.property_time_zone_catalog_resolutions
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.reject_property_time_zone_catalog_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE
                    properties.property_time_zone_operations,
                    properties.property_time_zone_catalog_resolutions,
                    properties.property_time_zone_catalog_entries,
                    properties.property_time_zone_transaction_proofs,
                    properties.properties,
                    properties.rooms,
                    properties.property_governance_revisions,
                    properties.property_mutation_operations,
                    properties.property_operation_locks,
                    properties.room_operation_locks,
                    properties.tenant_revisions,
                    properties.tenant_destroy_operations,
                    properties.tenant_destroy_receipts,
                    properties.inbox_messages,
                    properties.outbox_messages
                IN ACCESS EXCLUSIVE MODE NOWAIT;

                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM properties.property_time_zone_operations)
                    THEN
                        RAISE EXCEPTION
                            'Cannot remove property time-zone operations after native history has been recorded.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM properties.tenant_destroy_operations) OR
                       EXISTS (
                        SELECT 1
                        FROM properties.tenant_revisions
                        WHERE "LifecycleStatus" = 2)
                    THEN
                        RAISE EXCEPTION
                            'Cannot remove property time-zone operations while tenant destruction is in flight.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM properties.tenant_revisions
                        WHERE "Revision" = 9223372036854775807)
                    THEN
                        RAISE EXCEPTION
                            'Cannot invalidate Properties tenant revisions because a revision is exhausted.'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $migration$;

                WITH scopes AS (
                    SELECT "ScopeId" FROM properties.tenant_revisions
                    UNION SELECT "ScopeId" FROM properties.properties
                    UNION SELECT "ScopeId" FROM properties.rooms
                    UNION SELECT "ScopeId" FROM properties.property_governance_revisions
                    UNION SELECT "ScopeId" FROM properties.property_mutation_operations
                    UNION SELECT "ScopeId" FROM properties.property_operation_locks
                    UNION SELECT "ScopeId" FROM properties.room_operation_locks
                    UNION SELECT "ScopeId" FROM properties.tenant_destroy_receipts
                    UNION SELECT "ScopeId" FROM properties.inbox_messages
                    UNION SELECT "ScopeId" FROM properties.outbox_messages
                )
                INSERT INTO properties.tenant_revisions AS revisions
                    ("ScopeId", "Revision", "LifecycleStatus")
                SELECT "ScopeId", 1, 1
                FROM scopes
                WHERE "ScopeId" IS NOT NULL
                ON CONFLICT ("ScopeId") DO UPDATE
                SET "Revision" = revisions."Revision" + 1;

                DROP TRIGGER IF EXISTS
                    property_time_zone_operations_insert_provenance
                ON properties.property_time_zone_operations;

                DROP FUNCTION IF EXISTS
                    properties.validate_property_time_zone_operation_insert();

                DROP TRIGGER IF EXISTS
                    properties_require_time_zone_operation
                ON properties.properties;

                DROP FUNCTION IF EXISTS
                    properties.require_property_time_zone_operation();

                DROP TRIGGER IF EXISTS
                    properties_record_time_zone_transaction_proof
                ON properties.properties;

                DROP FUNCTION IF EXISTS
                    properties.record_property_time_zone_transaction_proof();

                DROP TRIGGER IF EXISTS
                    property_time_zone_transaction_proofs_trigger_owned
                ON properties.property_time_zone_transaction_proofs;

                DROP FUNCTION IF EXISTS
                    properties.guard_property_time_zone_transaction_proof();

                DROP TABLE IF EXISTS
                    properties.property_time_zone_transaction_proofs;

                DROP TRIGGER IF EXISTS
                    property_time_zone_operations_append_only
                ON properties.property_time_zone_operations;

                DROP FUNCTION IF EXISTS
                    properties.reject_property_time_zone_operation_mutation();

                DROP TRIGGER IF EXISTS
                    property_time_zone_catalog_entries_immutable
                ON properties.property_time_zone_catalog_entries;

                DROP TRIGGER IF EXISTS
                    property_time_zone_catalog_resolutions_immutable
                ON properties.property_time_zone_catalog_resolutions;

                DROP FUNCTION IF EXISTS
                    properties.reject_property_time_zone_catalog_mutation();
                """);

            migrationBuilder.DropTable(
                name: "property_time_zone_operations",
                schema: "properties");

            migrationBuilder.DropTable(
                name: "property_time_zone_catalog_resolutions",
                schema: "properties");

            migrationBuilder.DropTable(
                name: "property_time_zone_catalog_entries",
                schema: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_properties_tenant_destroy_operation_progress",
                schema: "properties",
                table: "tenant_destroy_operations");

            migrationBuilder.DropIndex(
                name: "IX_properties_scope_projection_ordinal_id",
                schema: "properties",
                table: "properties");

            migrationBuilder.AddCheckConstraint(
                name: "CK_properties_tenant_destroy_operation_progress",
                schema: "properties",
                table: "tenant_destroy_operations",
                sql: "\"Stage\" BETWEEN 1 AND 11 AND \"RemovedRecordCount\" >= 0 AND \"CompletedBatchCount\" >= 0 AND \"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
        }
    }
}
