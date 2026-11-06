using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunkFy.Modules.Guests.Persistence.PostgreSqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestProfileAuthoritativeStateIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"Status\" <> 3 OR (\"DisplayName\" = 'Anonymised guest' AND \"DisplayNameSearch\" = 'ANONYMISED GUEST' AND \"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND \"Email\" IS NULL AND \"EmailSearch\" IS NULL AND \"Phone\" IS NULL AND \"PhoneSearch\" IS NULL AND \"DateOfBirth\" IS NULL AND \"NationalityCountryCode\" IS NULL AND \"PreferredLanguageTag\" IS NULL AND \"Notes\" IS NULL AND \"CreationConfirmationId\" IS NULL AND \"ArchivedAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_coordinates",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000' AND \"OriginPropertyId\" <> '00000000-0000-0000-0000-000000000000' AND (\"CreationConfirmationId\" IS NULL OR \"CreationConfirmationId\" <> '00000000-0000-0000-0000-000000000000') AND trim(\"ScopeId\") <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_optional_text",
                schema: "guests",
                table: "guest_profiles",
                sql: "(\"NationalityCountryCode\" IS NULL OR trim(\"NationalityCountryCode\") <> '') AND (\"PreferredLanguageTag\" IS NULL OR trim(\"PreferredLanguageTag\") <> '') AND (\"Notes\" IS NULL OR trim(\"Notes\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_projection_ordinal",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"ProjectionOrdinal\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_search_shape",
                schema: "guests",
                table: "guest_profiles",
                sql: "trim(\"DisplayNameSearch\") <> '' AND ((\"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL) OR (\"LegalName\" IS NOT NULL AND \"LegalNameSearch\" IS NOT NULL AND trim(\"LegalName\") <> '' AND trim(\"LegalNameSearch\") <> '')) AND ((\"Email\" IS NULL AND \"EmailSearch\" IS NULL) OR (\"Email\" IS NOT NULL AND \"EmailSearch\" IS NOT NULL AND trim(\"Email\") <> '' AND trim(\"EmailSearch\") <> '')) AND ((\"Phone\" IS NULL AND \"PhoneSearch\" IS NULL) OR (\"Phone\" IS NOT NULL AND \"PhoneSearch\" IS NOT NULL AND trim(\"Phone\") <> '' AND trim(\"PhoneSearch\") <> ''))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_state_versions",
                schema: "guests",
                table: "guest_profiles",
                sql: "(\"Status\" = 1 AND \"Version\" >= 1) OR (\"Status\" IN (2, 3) AND \"Version\" >= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_guest_profiles_timestamps",
                schema: "guests",
                table: "guest_profiles",
                sql: "\"LastChangedAtUtc\" >= \"CreatedAtUtc\" AND (\"ArchivedAtUtc\" IS NULL OR \"ArchivedAtUtc\" = \"LastChangedAtUtc\") AND (\"AnonymisedAtUtc\" IS NULL OR (\"AnonymisedAtUtc\" >= \"CreatedAtUtc\" AND \"AnonymisedAtUtc\" <= \"LastChangedAtUtc\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_anonymised_profile",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_coordinates",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_optional_text",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_projection_ordinal",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_search_shape",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_state_versions",
                schema: "guests",
                table: "guest_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guest_profiles_timestamps",
                schema: "guests",
                table: "guest_profiles");
        }
    }
}
