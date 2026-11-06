using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class BindDataRightsRestrictionReleaseTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_cases_restriction_execution_proof",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.AddColumn<string>(
                name: "RestrictionTargetOwnerKey",
                schema: "data-rights",
                table: "cases",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RestrictionTargetOwnerOperationId",
                schema: "data-rights",
                table: "cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RestrictionTargetOwnerOperationVersion",
                schema: "data-rights",
                table: "cases",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RestrictionTargetSelectedAtUtc",
                schema: "data-rights",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestrictionTargetSelectedBy",
                schema: "data-rights",
                table: "cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestrictionTargetingContractVersion",
                schema: "data-rights",
                table: "cases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_cases_restriction_execution_proof",
                schema: "data-rights",
                table: "cases",
                sql: "((\"RequestedOperations\" = 4 AND \"Status\" = 9) AND \"RestrictionExecutionIdempotencyKey\" IS NOT NULL AND \"RestrictionExecutionApprovalRevision\" = \"DecisionRevision\" AND \"RestrictionExecutionDirective\" = \"RestrictionDirective\" AND length(trim(\"RestrictionExecutionOwnerKey\")) > 0 AND length(trim(\"RestrictionExecutionRecordType\")) > 0 AND \"RestrictionExecutionRecordId\" IS NOT NULL AND \"RestrictionExecutionSelectedRecordVersion\" >= 1 AND \"RestrictionExecutionReceiptContractVersion\" >= 1 AND \"RestrictionExecutionReceiptId\" IS NOT NULL AND \"RestrictionExecutionOwnerOperationId\" IS NOT NULL AND \"RestrictionExecutionResultingOwnerRevision\" >= 1 AND \"RestrictionExecutionResultingProjectionRevision\" >= 1 AND ((\"RestrictionExecutionDirective\" = 1 AND \"RestrictionTargetingContractVersion\" IS NULL AND \"RestrictionExecutionEffectiveRestricted\" = TRUE) OR (\"RestrictionExecutionDirective\" = 2 AND ((\"RestrictionTargetingContractVersion\" IS NULL AND \"RestrictionExecutionEffectiveRestricted\" = FALSE) OR (\"RestrictionTargetingContractVersion\" = 1 AND \"RestrictionExecutionOwnerOperationId\" = \"RestrictionTargetOwnerOperationId\" AND \"RestrictionExecutionResultingOwnerRevision\" = \"RestrictionTargetOwnerOperationVersion\" + 1)))) AND \"RestrictionExecutionReceiptSha256\" IS NOT NULL AND char_length(\"RestrictionExecutionReceiptSha256\") = 64 AND length(trim(\"RestrictionExecutionExecutedBy\")) > 0 AND \"RestrictionExecutionCompletedAtUtc\" >= \"DecidedAtUtc\" AND \"RestrictionExecutionCompletedAtUtc\" <= \"LastChangedAtUtc\") OR ((\"RequestedOperations\" <> 4 OR \"Status\" <> 9) AND \"RestrictionExecutionIdempotencyKey\" IS NULL AND \"RestrictionExecutionApprovalRevision\" IS NULL AND \"RestrictionExecutionDirective\" IS NULL AND \"RestrictionExecutionOwnerKey\" IS NULL AND \"RestrictionExecutionRecordType\" IS NULL AND \"RestrictionExecutionRecordId\" IS NULL AND \"RestrictionExecutionSelectedRecordVersion\" IS NULL AND \"RestrictionExecutionReceiptContractVersion\" IS NULL AND \"RestrictionExecutionReceiptId\" IS NULL AND \"RestrictionExecutionOwnerOperationId\" IS NULL AND \"RestrictionExecutionResultingOwnerRevision\" IS NULL AND \"RestrictionExecutionResultingProjectionRevision\" IS NULL AND \"RestrictionExecutionEffectiveRestricted\" IS NULL AND \"RestrictionExecutionReceiptSha256\" IS NULL AND \"RestrictionExecutionExecutedBy\" IS NULL AND \"RestrictionExecutionCompletedAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_cases_restriction_target",
                schema: "data-rights",
                table: "cases",
                sql: "(\"RestrictionTargetingContractVersion\" IS NULL AND \"RestrictionTargetOwnerKey\" IS NULL AND \"RestrictionTargetOwnerOperationId\" IS NULL AND \"RestrictionTargetOwnerOperationVersion\" IS NULL AND \"RestrictionTargetSelectedBy\" IS NULL AND \"RestrictionTargetSelectedAtUtc\" IS NULL) OR (\"RequestedOperations\" = 4 AND \"RestrictionDirective\" = 2 AND \"RestrictionTargetingContractVersion\" = 1 AND ((\"Status\" IN (1, 2, 11) AND \"RestrictionTargetOwnerKey\" IS NULL AND \"RestrictionTargetOwnerOperationId\" IS NULL AND \"RestrictionTargetOwnerOperationVersion\" IS NULL AND \"RestrictionTargetSelectedBy\" IS NULL AND \"RestrictionTargetSelectedAtUtc\" IS NULL) OR (length(trim(\"RestrictionTargetOwnerKey\")) > 0 AND \"RestrictionTargetOwnerOperationId\" IS NOT NULL AND \"RestrictionTargetOwnerOperationVersion\" BETWEEN 1 AND 9223372036854775806 AND length(trim(\"RestrictionTargetSelectedBy\")) > 0 AND \"RestrictionTargetSelectedAtUtc\" >= \"CreatedAtUtc\" AND \"RestrictionTargetSelectedAtUtc\" <= \"LastChangedAtUtc\")))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DO $$ BEGIN " +
                "IF EXISTS (SELECT 1 FROM \"data-rights\".\"cases\" " +
                "WHERE \"RestrictionTargetingContractVersion\" IS NOT NULL) THEN " +
                "RAISE EXCEPTION 'Cannot remove restriction target binding while target-bound Data Rights cases exist.'; " +
                "END IF; END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_cases_restriction_execution_proof",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_rights_cases_restriction_target",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetOwnerKey",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetOwnerOperationId",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetOwnerOperationVersion",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetSelectedAtUtc",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetSelectedBy",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "RestrictionTargetingContractVersion",
                schema: "data-rights",
                table: "cases");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_rights_cases_restriction_execution_proof",
                schema: "data-rights",
                table: "cases",
                sql: "((\"RequestedOperations\" = 4 AND \"Status\" = 9) AND \"RestrictionExecutionIdempotencyKey\" IS NOT NULL AND \"RestrictionExecutionApprovalRevision\" = \"DecisionRevision\" AND \"RestrictionExecutionDirective\" = \"RestrictionDirective\" AND length(trim(\"RestrictionExecutionOwnerKey\")) > 0 AND length(trim(\"RestrictionExecutionRecordType\")) > 0 AND \"RestrictionExecutionRecordId\" IS NOT NULL AND \"RestrictionExecutionSelectedRecordVersion\" >= 1 AND \"RestrictionExecutionReceiptContractVersion\" >= 1 AND \"RestrictionExecutionReceiptId\" IS NOT NULL AND \"RestrictionExecutionOwnerOperationId\" IS NOT NULL AND \"RestrictionExecutionResultingOwnerRevision\" >= 1 AND \"RestrictionExecutionResultingProjectionRevision\" >= 1 AND ((\"RestrictionExecutionDirective\" = 1 AND \"RestrictionExecutionEffectiveRestricted\" = TRUE) OR (\"RestrictionExecutionDirective\" = 2 AND \"RestrictionExecutionEffectiveRestricted\" = FALSE)) AND \"RestrictionExecutionReceiptSha256\" IS NOT NULL AND char_length(\"RestrictionExecutionReceiptSha256\") = 64 AND length(trim(\"RestrictionExecutionExecutedBy\")) > 0 AND \"RestrictionExecutionCompletedAtUtc\" >= \"DecidedAtUtc\" AND \"RestrictionExecutionCompletedAtUtc\" <= \"LastChangedAtUtc\") OR ((\"RequestedOperations\" <> 4 OR \"Status\" <> 9) AND \"RestrictionExecutionIdempotencyKey\" IS NULL AND \"RestrictionExecutionApprovalRevision\" IS NULL AND \"RestrictionExecutionDirective\" IS NULL AND \"RestrictionExecutionOwnerKey\" IS NULL AND \"RestrictionExecutionRecordType\" IS NULL AND \"RestrictionExecutionRecordId\" IS NULL AND \"RestrictionExecutionSelectedRecordVersion\" IS NULL AND \"RestrictionExecutionReceiptContractVersion\" IS NULL AND \"RestrictionExecutionReceiptId\" IS NULL AND \"RestrictionExecutionOwnerOperationId\" IS NULL AND \"RestrictionExecutionResultingOwnerRevision\" IS NULL AND \"RestrictionExecutionResultingProjectionRevision\" IS NULL AND \"RestrictionExecutionEffectiveRestricted\" IS NULL AND \"RestrictionExecutionReceiptSha256\" IS NULL AND \"RestrictionExecutionExecutedBy\" IS NULL AND \"RestrictionExecutionCompletedAtUtc\" IS NULL)");
        }
    }
}
