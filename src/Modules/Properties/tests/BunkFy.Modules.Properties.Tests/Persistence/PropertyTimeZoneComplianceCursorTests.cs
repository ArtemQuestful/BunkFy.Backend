namespace BunkFy.Modules.Properties.Tests;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Properties.Persistence.Repositories;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneComplianceCursorTests
{
    private const string TenantId = "tenant-a";
    private const string CatalogVersion = "TZDB: test-v1";

    [Fact]
    public void Cursor_round_trips_only_for_the_bound_tenant()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");

        string cursor = PropertyTimeZoneComplianceCursor.Create(
            TenantId,
            CatalogVersion,
            projectionOrdinal: 42,
            propertyId);

        Assert.StartsWith("ptzc1_", cursor, StringComparison.Ordinal);
        PropertyTimeZoneCompliancePosition position = Assert.IsType<
            PropertyTimeZoneCompliancePosition>(
            PropertyTimeZoneComplianceCursor.Parse(
                cursor,
                TenantId,
                CatalogVersion));
        Assert.Equal(42, position.ProjectionOrdinal);
        Assert.Equal(propertyId, position.PropertyId);
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                cursor,
                "tenant-b",
                CatalogVersion));
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                cursor,
                TenantId,
                "TZDB: test-v2"));
    }

    [Fact]
    public void Cursor_rejects_changed_or_unknown_envelopes()
    {
        string cursor = PropertyTimeZoneComplianceCursor.Create(
            TenantId,
            CatalogVersion,
            projectionOrdinal: 42,
            Guid.Parse("10000000-0000-0000-0000-000000000001"));
        const int changedIndex = 10;
        char replacement = cursor[changedIndex] == 'A' ? 'B' : 'A';
        string changed = cursor[..changedIndex] + replacement +
            cursor[(changedIndex + 1)..];

        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                changed,
                TenantId,
                CatalogVersion));
        Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                "ptzc2_not-a-supported-cursor",
                TenantId,
                CatalogVersion));
    }

    [Fact]
    public void Cursor_rejects_noncanonical_or_truncated_payloads()
    {
        string cursor = PropertyTimeZoneComplianceCursor.Create(
            TenantId,
            CatalogVersion,
            projectionOrdinal: 42,
            Guid.Parse("10000000-0000-0000-0000-000000000001"));
        int digestSeparator = cursor.LastIndexOf('.');
        string noncanonical = cursor.Insert(digestSeparator - 1, " ");

        ArgumentException whitespace = Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                noncanonical,
                TenantId,
                CatalogVersion));
        Assert.Equal("cursor", whitespace.ParamName);

        string truncated = CreateEnvelope($"{TenantId.Length}:x");
        ArgumentException shortPayload = Assert.Throws<ArgumentException>(() =>
            PropertyTimeZoneComplianceCursor.Parse(
                truncated,
                TenantId,
                CatalogVersion));
        Assert.Equal("cursor", shortPayload.ParamName);
    }

    [Fact]
    public void Empty_cursor_starts_a_new_scan()
    {
        Assert.Null(PropertyTimeZoneComplianceCursor.Parse(
            null,
            TenantId,
            CatalogVersion));
        Assert.Null(PropertyTimeZoneComplianceCursor.Parse(
            "  ",
            TenantId,
            CatalogVersion));
    }

    private static string CreateEnvelope(string payload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string encoded = Convert.ToBase64String(payloadBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "bunkfy-properties-time-zone-compliance-cursor/v1|" +
                payload)));
        return $"ptzc1_{encoded}.{digest}";
    }
}
