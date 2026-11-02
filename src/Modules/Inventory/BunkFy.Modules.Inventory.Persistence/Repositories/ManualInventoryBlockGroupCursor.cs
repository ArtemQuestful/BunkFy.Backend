namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using System.Buffers.Binary;
using BunkFy.Modules.Inventory.Contracts;

internal static class ManualInventoryBlockGroupCursor
{
    private const byte Version = 1;
    private const byte GroupKind = 1;
    private const byte MemberKind = 2;

    public static string EncodeGroup(
        Guid propertyId,
        ManualInventoryBlockGroupStatus? status,
        DateTimeOffset createdAtUtc,
        Guid blockGroupId)
    {
        Span<byte> payload = stackalloc byte[47];
        payload[0] = Version;
        payload[1] = GroupKind;
        propertyId.TryWriteBytes(payload[2..18], bigEndian: true, out _);
        payload[18] = checked((byte)(status.HasValue
            ? (int)status.Value
            : 0));
        BinaryPrimitives.WriteInt64BigEndian(
            payload[19..27],
            createdAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt32BigEndian(
            payload[27..31],
            checked((int)createdAtUtc.Offset.TotalMinutes));
        blockGroupId.TryWriteBytes(payload[31..47], bigEndian: true, out _);
        return Base64UrlEncode(payload);
    }

    public static (DateTimeOffset CreatedAtUtc, Guid BlockGroupId)
        DecodeGroup(
            string cursor,
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status)
    {
        byte[] payload = Base64UrlDecode(cursor);
        if (payload.Length != 47 || payload[0] != Version ||
            payload[1] != GroupKind ||
            new Guid(payload.AsSpan(2, 16), bigEndian: true) != propertyId ||
            payload[18] != (byte)(status.HasValue ? (int)status.Value : 0))
        {
            throw InvalidCursor();
        }

        try
        {
            long utcTicks = BinaryPrimitives.ReadInt64BigEndian(
                payload.AsSpan(19, 8));
            int offsetMinutes = BinaryPrimitives.ReadInt32BigEndian(
                payload.AsSpan(27, 4));
            DateTimeOffset createdAtUtc = new(
                new DateTime(utcTicks, DateTimeKind.Utc));
            if (offsetMinutes != 0)
            {
                createdAtUtc = createdAtUtc.ToOffset(
                    TimeSpan.FromMinutes(offsetMinutes));
            }

            return (
                createdAtUtc,
                new Guid(payload.AsSpan(31, 16), bigEndian: true));
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            throw InvalidCursor();
        }
    }

    public static string EncodeMember(
        Guid propertyId,
        Guid blockGroupId,
        ManualInventoryBlockStatus? status,
        Guid blockId)
    {
        Span<byte> payload = stackalloc byte[52];
        payload[0] = Version;
        payload[1] = MemberKind;
        propertyId.TryWriteBytes(payload[2..18], bigEndian: true, out _);
        blockGroupId.TryWriteBytes(payload[18..34], bigEndian: true, out _);
        payload[34] = checked((byte)(status.HasValue
            ? (int)status.Value
            : 0));
        payload[35] = 0;
        blockId.TryWriteBytes(payload[36..52], bigEndian: true, out _);
        return Base64UrlEncode(payload);
    }

    public static Guid DecodeMember(
        string cursor,
        Guid propertyId,
        Guid blockGroupId,
        ManualInventoryBlockStatus? status)
    {
        byte[] payload = Base64UrlDecode(cursor);
        if (payload.Length != 52 || payload[0] != Version ||
            payload[1] != MemberKind || payload[35] != 0 ||
            new Guid(payload.AsSpan(2, 16), bigEndian: true) != propertyId ||
            new Guid(payload.AsSpan(18, 16), bigEndian: true) != blockGroupId ||
            payload[34] != (byte)(status.HasValue ? (int)status.Value : 0))
        {
            throw InvalidCursor();
        }

        return new Guid(payload.AsSpan(36, 16), bigEndian: true);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> payload) =>
        Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 128 ||
            cursor.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_')))
        {
            throw InvalidCursor();
        }

        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw InvalidCursor()
        };
        try
        {
            byte[] payload = Convert.FromBase64String(padded);
            if (!string.Equals(
                    Base64UrlEncode(payload),
                    cursor,
                    StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            return payload;
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static FormatException InvalidCursor() =>
        new("The manual block-group cursor is invalid.");
}
