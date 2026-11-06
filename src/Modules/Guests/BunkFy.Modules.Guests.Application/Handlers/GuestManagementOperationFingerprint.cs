namespace BunkFy.Modules.Guests.Application.Handlers;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Domain.ValueObjects;

internal static class GuestManagementOperationFingerprint
{
    public static string Update(
        Guid propertyId,
        Guid guestId,
        long expectedVersion,
        GuestProfileChange values)
    {
        string?[] fields =
        [
            values.LanguageTags is null ? "guest-profile-update-v1" : "guest-profile-update-v2",
            propertyId.ToString("N"),
            guestId.ToString("N"),
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            values.DisplayName,
            values.LegalName,
            values.Email,
            values.Phone,
            values.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            values.NationalityCountryCode,
            values.PreferredLanguageTag,
            values.Notes
        ];
        return values.LanguageTags is null
            ? Compute(fields)
            : Compute([.. fields, "language-tags-present",
                values.LanguageTags.Length.ToString(CultureInfo.InvariantCulture),
                .. values.LanguageTags.Select(GuestLanguageTags.Identity).Order(StringComparer.Ordinal)]);
    }

    private static string Compute(params string?[] values)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (string? value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
