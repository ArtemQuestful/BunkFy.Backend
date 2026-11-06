namespace BunkFy.Modules.Workspaces.Application.Handlers;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static class WorkspaceStaffOnboardingRecoveryFingerprint
{
    private const string Domain =
        "bunkfy.workspaces.staff-onboarding-recovery.request.v1";

    public static string Compute(
        Guid applicationId,
        Guid operationId,
        long expectedApplicationVersion,
        string recoveryActorId)
    {
        using MemoryStream canonical = new();
        Append(canonical, Domain);
        Append(canonical, applicationId.ToString("D"));
        Append(canonical, operationId.ToString("D"));
        Append(canonical, expectedApplicationVersion.ToString(
            CultureInfo.InvariantCulture));
        Append(canonical, recoveryActorId.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
    }

    public static string ComputeTargetSet(
        IEnumerable<string> scopeValues)
    {
        using MemoryStream canonical = new();
        Append(
            canonical,
            "bunkfy.workspaces.staff-onboarding-recovery.targets.v1");
        string[] values = scopeValues
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Append(canonical, values.Length.ToString(CultureInfo.InvariantCulture));
        foreach (string value in values)
        {
            Append(canonical, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
    }

    private static void Append(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
