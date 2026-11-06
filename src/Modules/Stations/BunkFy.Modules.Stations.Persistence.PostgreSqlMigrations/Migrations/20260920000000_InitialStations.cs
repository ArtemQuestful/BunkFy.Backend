using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Stations.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialStations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "stations");

            migrationBuilder.CreateTable(
                name: "operation_receipts",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ActorSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<long>(type: "bigint", nullable: true),
                    RetryAfterUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_receipts", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_receipt_kind", "\"Kind\" BETWEEN 1 AND 7 AND \"Outcome\" BETWEEN 1 AND 5");
                });

            migrationBuilder.CreateTable(
                name: "staff_check_in_grants",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_check_in_grants", x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
                    table.CheckConstraint("CK_grant_revision", "\"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "staff_credentials",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AlgorithmVersion = table.Column<int>(type: "integer", nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    Salt = table.Column<string>(type: "character varying(88)", maxLength: 88, nullable: false),
                    Verifier = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    PepperVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureWindowStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    CooldownUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_credentials", x => new { x.ScopeId, x.StaffMemberId });
                    table.CheckConstraint("CK_credential_failure", "\"FailureCount\" >= 0");
                    table.CheckConstraint("CK_credential_version", "\"Revision\" > 0 AND \"AlgorithmVersion\" = 1 AND \"Iterations\" = 600000");
                });

            migrationBuilder.CreateTable(
                name: "stations",
                schema: "stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stations", x => new { x.ScopeId, x.Id });
                    table.UniqueConstraint("AK_stations_ScopeId_Id_PropertyId", x => new { x.ScopeId, x.Id, x.PropertyId });
                    table.CheckConstraint("CK_station_version", "\"Version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "tenant_lifecycle",
                schema: "stations",
                columns: table => new
                {
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Closed = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_lifecycle", x => x.ScopeId);
                });

            migrationBuilder.CreateTable(
                name: "browser_sessions",
                schema: "stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalEpoch = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PairingExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorityKind = table.Column<int>(type: "integer", nullable: false),
                    CredentialRevision = table.Column<long>(type: "bigint", nullable: true),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: true),
                    ActorIdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActorAbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptWindowStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CooldownUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_browser_sessions", x => new { x.ScopeId, x.Id });
                    table.UniqueConstraint("AK_browser_sessions_ScopeId_Id_StationId_PropertyId", x => new { x.ScopeId, x.Id, x.StationId, x.PropertyId });
                    table.CheckConstraint("CK_browser_actor", "(\"StaffMemberId\" IS NULL AND \"ActorSessionId\" IS NULL AND \"AuthorityKind\" = 0 AND \"CredentialRevision\" IS NULL AND \"GrantRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NULL) OR (\"StaffMemberId\" IS NOT NULL AND \"ActorSessionId\" IS NOT NULL AND \"CredentialRevision\" IS NOT NULL AND \"CredentialRevision\" > 0 AND \"ActorIdleExpiresAtUtc\" IS NOT NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NOT NULL AND ((\"AuthorityKind\" = 1 AND \"GrantRevision\" IS NULL) OR (\"AuthorityKind\" = 2 AND \"GrantRevision\" IS NOT NULL AND \"GrantRevision\" > 0)))");
                    table.CheckConstraint("CK_browser_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_browser_lifetime", "\"PairingExpiresAtUtc\" > \"IssuedAtUtc\" AND \"ExternalEpoch\" > 0 AND \"Generation\" > 0");
                    table.ForeignKey(
                        name: "FK_browser_sessions_stations_ScopeId_StationId_PropertyId",
                        columns: x => new { x.ScopeId, x.StationId, x.PropertyId },
                        principalSchema: "stations",
                        principalTable: "stations",
                        principalColumns: new[] { "ScopeId", "Id", "PropertyId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "setup_grants",
                schema: "stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrowserSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityKind = table.Column<int>(type: "integer", nullable: false),
                    ExpectedCredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_grants", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_setup_valid", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ExpectedCredentialRevision\" >= 0 AND \"AuthorityKind\" IN (1,2)");
                    table.ForeignKey(
                        name: "FK_setup_grants_browser_sessions_ScopeId_BrowserSessionId_Stat~",
                        columns: x => new { x.ScopeId, x.BrowserSessionId, x.StationId, x.PropertyId },
                        principalSchema: "stations",
                        principalTable: "browser_sessions",
                        principalColumns: new[] { "ScopeId", "Id", "StationId", "PropertyId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_CredentialDigest",
                schema: "stations",
                table: "browser_sessions",
                column: "CredentialDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_ScopeId_StaffMemberId",
                schema: "stations",
                table: "browser_sessions",
                columns: new[] { "ScopeId", "StaffMemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_ScopeId_StationId_PropertyId",
                schema: "stations",
                table: "browser_sessions",
                columns: new[] { "ScopeId", "StationId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_setup_grants_ScopeId_BrowserSessionId_StationId_PropertyId",
                schema: "stations",
                table: "setup_grants",
                columns: new[] { "ScopeId", "BrowserSessionId", "StationId", "PropertyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_receipts",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "setup_grants",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "staff_check_in_grants",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "staff_credentials",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "tenant_lifecycle",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "browser_sessions",
                schema: "stations");

            migrationBuilder.DropTable(
                name: "stations",
                schema: "stations");
        }
    }
}
