namespace BunkFy.Extensions.Operations.Notifications;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Modules.Notifications.Contracts;

internal static class
    OperationsNotificationsStaffHistoryPolicyEvidence
{
    public static DataRightsApprovalEvidenceBinding CreateBinding(
        NotificationHistoryReference reference,
        NotificationHistoryReferenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            OperationsNotificationsDataRightsCoordinates
                .StaffHistoryStateBindingKey,
            snapshot.Version,
            ComputeSnapshotSha256(reference, snapshot));
    }

    public static bool MatchesFrozenBinding(
        DataRightsApprovalEvidence evidence,
        NotificationHistoryReference reference,
        NotificationHistoryReferenceSnapshot snapshot) =>
        TryGetBinding(
            evidence,
            out DataRightsApprovalEvidenceBinding? binding) &&
        binding!.Version == snapshot.Version &&
        string.Equals(
            binding.Sha256,
            ComputeSnapshotSha256(reference, snapshot),
            StringComparison.Ordinal);

    public static bool TryGetBinding(
        DataRightsApprovalEvidence? evidence,
        out DataRightsApprovalEvidenceBinding? binding)
    {
        DataRightsApprovalEvidenceBinding[] matches =
            (evidence?.StateBindings ?? [])
            .Where(candidate =>
                candidate is not null &&
                string.Equals(
                    candidate.Key,
                    OperationsNotificationsDataRightsCoordinates
                        .StaffHistoryStateBindingKey,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        binding = matches.Length == 1 ? matches[0] : null;
        return binding is not null &&
            binding.Version > 0 &&
            OperationsNotificationsDataRightsValidation.IsSha256(
                binding.Sha256);
    }

    public static string ComputeSnapshotSha256(
        NotificationHistoryReference reference,
        NotificationHistoryReferenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(snapshot);
        string canonical = string.Join(
            '|',
            "bunkfy-operations-notifications-staff-history-snapshot/v1",
            reference.Namespace,
            reference.Digest,
            V1StatusCode(snapshot.Status).ToString(
                CultureInfo.InvariantCulture),
            snapshot.Version.ToString(CultureInfo.InvariantCulture),
            snapshot.RecordCount.ToString(CultureInfo.InvariantCulture),
            snapshot.LatestStreamSequence.ToString(
                CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static int V1StatusCode(NotificationHistoryReferenceStatus status) =>
        status switch
        {
            NotificationHistoryReferenceStatus.Missing => 0,
            NotificationHistoryReferenceStatus.Open => 1,
            NotificationHistoryReferenceStatus.Closed => 2,
            _ => throw new InvalidOperationException(
                "The notification history status has no v1 evidence code.")
        };
}
