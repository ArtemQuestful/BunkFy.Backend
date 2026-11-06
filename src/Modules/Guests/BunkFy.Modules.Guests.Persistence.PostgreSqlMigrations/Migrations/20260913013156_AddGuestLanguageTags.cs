using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestLanguageTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_changed_fields",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_contract",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.AddColumn<string[]>(
                name: "LanguageTags",
                schema: "guests",
                table: "guest_profiles",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            // Preserve every accepted legacy scalar byte; identity normalization applies to future requests only.
            migrationBuilder.Sql("""
                UPDATE guests.guest_profiles
                SET "LanguageTags" = ARRAY["PreferredLanguageTag"]
                WHERE "PreferredLanguageTag" IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"Status\" <> 3 OR (\"DisplayName\" = 'Anonymised guest' AND \"DisplayNameSearch\" = 'ANONYMISED GUEST' AND \"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND \"Email\" IS NULL AND \"EmailSearch\" IS NULL AND \"Phone\" IS NULL AND \"PhoneSearch\" IS NULL AND \"DateOfBirth\" IS NULL AND \"NationalityCountryCode\" IS NULL AND \"PreferredLanguageTag\" IS NULL AND cardinality(\"LanguageTags\") = 0 AND \"Notes\" IS NULL AND \"CreationConfirmationId\" IS NULL AND \"ArchivedAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_language_tags",
                schema: "guests",
                table: "guest_profiles",
                sql: "cardinality(\"LanguageTags\") BETWEEN 0 AND 12 AND array_position(\"LanguageTags\", NULL) IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_changed_fields",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "(\"ContractVersion\" = 1 AND \"ChangedFieldsMask\" BETWEEN 1 AND 255) OR (\"ContractVersion\" = 2 AND \"ChangedFieldsMask\" BETWEEN 1 AND 511)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_contract",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "\"ContractVersion\" IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF retains these locks through the migration transaction and every destructive statement below.
            migrationBuilder.Sql("""
                LOCK TABLE guests.guest_profiles, guests.guest_data_rights_correction_receipts
                    IN ACCESS EXCLUSIVE MODE;
                DO $lossless$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM guests.guest_profiles
                        WHERE "LanguageTags" IS DISTINCT FROM CASE
                            WHEN "PreferredLanguageTag" IS NULL THEN ARRAY[]::text[]
                            ELSE ARRAY["PreferredLanguageTag"]::text[] END
                    ) OR EXISTS (
                        SELECT 1 FROM guests.guest_data_rights_correction_receipts
                        WHERE "ContractVersion" <> 1 OR "ChangedFieldsMask" NOT BETWEEN 1 AND 255
                    ) THEN
                        RAISE EXCEPTION 'Guest languages or correction receipts cannot be represented by the previous schema. Retain the compatible backend and use approved forward repair.';
                    END IF;
                END
                $lossless$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_language_tags",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_changed_fields",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_contract",
                schema: "guests",
                table: "guest_data_rights_correction_receipts");

            migrationBuilder.DropColumn(
                name: "LanguageTags",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"Status\" <> 3 OR (\"DisplayName\" = 'Anonymised guest' AND \"DisplayNameSearch\" = 'ANONYMISED GUEST' AND \"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND \"Email\" IS NULL AND \"EmailSearch\" IS NULL AND \"Phone\" IS NULL AND \"PhoneSearch\" IS NULL AND \"DateOfBirth\" IS NULL AND \"NationalityCountryCode\" IS NULL AND \"PreferredLanguageTag\" IS NULL AND \"Notes\" IS NULL AND \"CreationConfirmationId\" IS NULL AND \"ArchivedAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_changed_fields",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "\"ChangedFieldsMask\" BETWEEN 1 AND 255");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_data_rights_correction_receipts_contract",
                schema: "guests",
                table: "guest_data_rights_correction_receipts",
                sql: "\"ContractVersion\" = 1");
        }
    }
}
