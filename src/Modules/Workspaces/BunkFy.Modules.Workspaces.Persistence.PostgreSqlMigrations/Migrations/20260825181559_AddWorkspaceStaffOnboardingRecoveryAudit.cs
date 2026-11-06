using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Workspaces.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceStaffOnboardingRecoveryAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedMembershipId",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AcceptedMembershipVersion",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "staff_onboarding_recovery_receipts",
                schema: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerKind = table.Column<int>(type: "integer", nullable: false),
                    TriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicantSubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PlanCreatorActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecoveryActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EffectiveSystemActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ExpectedApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    SelectedApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    SelectedApplicationStatus = table.Column<int>(type: "integer", nullable: false),
                    ResultingApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ResultingApplicationStatus = table.Column<int>(type: "integer", nullable: false),
                    CurrentProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentProfileVersion = table.Column<long>(type: "bigint", nullable: false),
                    PropertyCount = table.Column<int>(type: "integer", nullable: false),
                    TargetSetSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OrganizationRevision = table.Column<long>(type: "bigint", nullable: true),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: true),
                    MembershipStatus = table.Column<int>(type: "integer", nullable: true),
                    StaffTenantRevision = table.Column<long>(type: "bigint", nullable: true),
                    StaffMemberVersion = table.Column<long>(type: "bigint", nullable: true),
                    StaffStateProofSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    StaffPropertySetSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    AccessManagementRevision = table.Column<long>(type: "bigint", nullable: true),
                    AccessFactsSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    OrganizationVerification = table.Column<int>(type: "integer", nullable: false),
                    StaffVerification = table.Column<int>(type: "integer", nullable: false),
                    AccessVerification = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    AccessMutationPerformed = table.Column<bool>(type: "boolean", nullable: false),
                    OrganizationObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StaffObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AccessObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_onboarding_recovery_receipts", x => x.Id);
                    table.UniqueConstraint("AK_staff_onboarding_recovery_receipts_ScopeId_Id", x => new { x.ScopeId, x.Id });
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_contract", "\"ContractVersion\" = 1");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_chronology", "\"RecordedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND (\"OrganizationObservedAtUtc\" IS NULL OR \"OrganizationObservedAtUtc\" <= \"RecordedAtUtc\") AND (\"StaffObservedAtUtc\" IS NULL OR \"StaffObservedAtUtc\" <= \"RecordedAtUtc\") AND (\"AccessObservedAtUtc\" IS NULL OR \"AccessObservedAtUtc\" <= \"RecordedAtUtc\")");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_digests", "char_length(\"RequestSha256\") = 64 AND char_length(\"TargetSetSha256\") = 64 AND (\"StaffStateProofSha256\" IS NULL OR char_length(\"StaffStateProofSha256\") = 64) AND (\"StaffPropertySetSha256\" IS NULL OR char_length(\"StaffPropertySetSha256\") = 64) AND (\"AccessFactsSha256\" IS NULL OR char_length(\"AccessFactsSha256\") = 64)");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_no_mutation", "\"AccessMutationPerformed\" = FALSE");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_outcome", "(\"Outcome\" = 1 AND \"Reason\" = 1) OR (\"Outcome\" = 2 AND \"Reason\" IN (2, 3, 5, 7, 9)) OR (\"Outcome\" = 3 AND \"Reason\" IN (4, 6, 8))");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_presence", "((\"OrganizationVerification\" = 0 AND \"OrganizationObservedAtUtc\" IS NULL AND \"OrganizationRevision\" IS NULL AND \"MembershipId\" IS NULL AND \"MembershipVersion\" IS NULL AND \"MembershipStatus\" IS NULL) OR (\"OrganizationVerification\" <> 0 AND \"OrganizationObservedAtUtc\" IS NOT NULL AND (\"OrganizationRevision\" IS NULL OR \"OrganizationRevision\" >= 0) AND ((\"MembershipId\" IS NULL AND \"MembershipVersion\" IS NULL AND \"MembershipStatus\" IS NULL) OR (\"MembershipId\" IS NOT NULL AND \"MembershipVersion\" > 0 AND \"MembershipStatus\" IS NOT NULL)) AND (\"OrganizationVerification\" <> 1 OR (\"OrganizationRevision\" IS NOT NULL AND \"MembershipId\" IS NOT NULL AND \"MembershipVersion\" > 0 AND \"MembershipStatus\" = 1)))) AND ((\"StaffVerification\" = 0 AND \"StaffObservedAtUtc\" IS NULL AND \"StaffTenantRevision\" IS NULL AND \"StaffMemberVersion\" IS NULL AND \"StaffStateProofSha256\" IS NULL AND \"StaffPropertySetSha256\" IS NULL) OR (\"StaffVerification\" <> 0 AND \"StaffObservedAtUtc\" IS NOT NULL AND (\"StaffTenantRevision\" IS NULL OR \"StaffTenantRevision\" >= 0) AND (\"StaffMemberVersion\" IS NULL OR \"StaffMemberVersion\" >= 1) AND (\"StaffVerification\" <> 1 OR (\"StaffTenantRevision\" IS NOT NULL AND \"StaffMemberVersion\" IS NOT NULL AND \"StaffStateProofSha256\" IS NOT NULL AND \"StaffPropertySetSha256\" IS NOT NULL)))) AND ((\"AccessVerification\" = 0 AND \"AccessObservedAtUtc\" IS NULL AND \"AccessManagementRevision\" IS NULL AND \"AccessFactsSha256\" IS NULL) OR (\"AccessVerification\" <> 0 AND \"AccessObservedAtUtc\" IS NOT NULL AND (\"AccessManagementRevision\" IS NULL OR \"AccessManagementRevision\" >= 0) AND (\"AccessVerification\" <> 1 OR (\"AccessManagementRevision\" IS NOT NULL AND \"AccessFactsSha256\" IS NOT NULL)))) AND (\"StaffVerification\" = 0 OR \"OrganizationVerification\" <> 0) AND (\"AccessVerification\" = 0 OR \"StaffVerification\" <> 0)");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_verification", "\"OrganizationVerification\" BETWEEN 0 AND 3 AND \"StaffVerification\" BETWEEN 0 AND 3 AND \"AccessVerification\" BETWEEN 0 AND 3 AND ((\"Reason\" = 1 AND \"OrganizationVerification\" = 1 AND \"StaffVerification\" = 1 AND \"AccessVerification\" = 1) OR (\"Reason\" IN (2, 9) AND \"OrganizationVerification\" = 0 AND \"StaffVerification\" = 0 AND \"AccessVerification\" = 0) OR (\"Reason\" = 3 AND \"OrganizationVerification\" = 2 AND \"StaffVerification\" IN (0, 1) AND \"AccessVerification\" IN (0, 1)) OR (\"Reason\" = 4 AND \"OrganizationVerification\" = 3 AND \"StaffVerification\" IN (0, 1) AND \"AccessVerification\" IN (0, 1)) OR (\"Reason\" = 5 AND \"OrganizationVerification\" = 1 AND \"StaffVerification\" = 2 AND \"AccessVerification\" IN (0, 1)) OR (\"Reason\" = 6 AND \"OrganizationVerification\" = 1 AND \"StaffVerification\" = 3 AND \"AccessVerification\" IN (0, 1)) OR (\"Reason\" = 7 AND \"OrganizationVerification\" = 1 AND \"StaffVerification\" = 1 AND \"AccessVerification\" = 2) OR (\"Reason\" = 8 AND \"OrganizationVerification\" = 1 AND \"StaffVerification\" = 1 AND \"AccessVerification\" = 3))");
                    table.CheckConstraint("CK_workspaces_staff_onboarding_recovery_receipts_versions", "\"ExpectedApplicationVersion\" >= 1 AND \"SelectedApplicationVersion\" >= 1 AND \"ExpectedApplicationVersion\" = \"SelectedApplicationVersion\" AND \"SelectedApplicationStatus\" = 6 AND ((\"Outcome\" = 1 AND \"ResultingApplicationVersion\" = \"SelectedApplicationVersion\" + 1 AND \"ResultingApplicationStatus\" = 5) OR (\"Outcome\" IN (2, 3) AND \"ResultingApplicationVersion\" = \"SelectedApplicationVersion\" AND \"ResultingApplicationStatus\" = 6))");
                    table.ForeignKey(
                        name: "FK_staff_onboarding_recovery_receipts_staff_onboarding_applica~",
                        columns: x => new { x.ScopeId, x.ApplicationId },
                        principalSchema: "workspaces",
                        principalTable: "staff_onboarding_applications",
                        principalColumns: new[] { "ScopeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_staff_onboarding_membership_binding",
                schema: "workspaces",
                table: "staff_onboarding_applications",
                sql: "(\"AcceptedMembershipId\" IS NULL AND \"AcceptedMembershipVersion\" IS NULL) OR (\"AcceptedMembershipId\" IS NOT NULL AND \"AcceptedMembershipVersion\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_recovery_receipts_ScopeId_ApplicationId_Re~",
                schema: "workspaces",
                table: "staff_onboarding_recovery_receipts",
                columns: new[] { "ScopeId", "ApplicationId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_recovery_receipts_ScopeId_OperationId",
                schema: "workspaces",
                table: "staff_onboarding_recovery_receipts",
                columns: new[] { "ScopeId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_onboarding_recovery_receipts_ScopeId_RecoveryActorId_~",
                schema: "workspaces",
                table: "staff_onboarding_recovery_receipts",
                columns: new[] { "ScopeId", "RecoveryActorId", "RecordedAtUtc", "Id" });

            migrationBuilder.Sql(
                """
                CREATE TRIGGER
                    "TR_staff_onboarding_recovery_receipts_append_only"
                BEFORE UPDATE OR DELETE
                ON workspaces.staff_onboarding_recovery_receipts
                FOR EACH ROW
                EXECUTE FUNCTION workspaces.prevent_receipt_mutation();
                """);
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
                        FROM workspaces.staff_onboarding_recovery_receipts
                        LIMIT 1
                    ) OR EXISTS (
                        SELECT 1
                        FROM workspaces.staff_onboarding_applications
                        WHERE "AcceptedMembershipId" IS NOT NULL
                           OR "AcceptedMembershipVersion" IS NOT NULL
                        LIMIT 1
                    ) THEN
                        RAISE EXCEPTION
                            'Workspaces onboarding recovery audit data exists; downgrade is refused.';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS
                    "TR_staff_onboarding_recovery_receipts_append_only"
                    ON workspaces.staff_onboarding_recovery_receipts;
                """);

            migrationBuilder.DropTable(
                name: "staff_onboarding_recovery_receipts",
                schema: "workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_staff_onboarding_membership_binding",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "AcceptedMembershipId",
                schema: "workspaces",
                table: "staff_onboarding_applications");

            migrationBuilder.DropColumn(
                name: "AcceptedMembershipVersion",
                schema: "workspaces",
                table: "staff_onboarding_applications");
        }
    }
}
