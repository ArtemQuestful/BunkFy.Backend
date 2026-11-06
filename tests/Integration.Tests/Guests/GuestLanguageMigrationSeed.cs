namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Persistence;
using Microsoft.EntityFrameworkCore;

internal static class GuestLanguageMigrationSeed
{
    // Historical schemas have no LanguageTags column. Do not seed them through the current EF model.
    public static async Task ProfilesAsync(GuestsDbContext db, params GuestProfile[] profiles)
    {
        foreach (GuestProfile profile in profiles)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO guests.guest_profiles (
                    "Id", "ScopeId", "OriginPropertyId", "CreationConfirmationId",
                    "DisplayName", "DisplayNameSearch", "LegalName", "LegalNameSearch",
                    "Email", "EmailSearch", "Phone", "PhoneSearch", "DateOfBirth",
                    "NationalityCountryCode", "PreferredLanguageTag", "Notes", "Status", "Version",
                    "CreatedBy", "CreatedAtUtc", "LastChangedBy", "LastChangedAtUtc",
                    "ArchivedAtUtc", "AnonymisedAtUtc")
                VALUES ({profile.Id}, {profile.ScopeId}, {profile.OriginPropertyId}, {profile.CreationConfirmationId},
                    {profile.DisplayName}, {profile.DisplayNameSearch}, {profile.LegalName}, {profile.LegalNameSearch},
                    {profile.Email}, {profile.EmailSearch}, {profile.Phone}, {profile.PhoneSearch}, {profile.DateOfBirth},
                    {profile.NationalityCountryCode}, {profile.PreferredLanguageTag}, {profile.Notes}, {(int)profile.Status}, {profile.Version},
                    {profile.CreatedBy}, {profile.CreatedAtUtc}, {profile.LastChangedBy}, {profile.LastChangedAtUtc},
                    {profile.ArchivedAtUtc}, {profile.AnonymisedAtUtc});
                """);
        }
    }
}
