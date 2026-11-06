namespace BunkFy.Modules.DataRights.Application;

using BunkFy.Modules.DataRights.Application.Ports;

internal static class TenantTerminationReplayDurability
{
    public static void EnsureMatches(
        TenantTerminationReplayAppendReceipt? receipt,
        TenantTerminationReplayJournalEntry? entry)
    {
        if (!Matches(receipt, entry))
        {
            throw new InvalidOperationException(
                "DataRights.TenantTerminationReplayProofInvalid");
        }
    }

    public static bool Matches(
        TenantTerminationReplayAppendReceipt? receipt,
        TenantTerminationReplayJournalEntry? entry) =>
        receipt is not null &&
        entry is not null &&
        entry.HasValidProof() &&
        receipt.ContractVersion ==
            TenantTerminationReplayAppendReceipt.CurrentContractVersion &&
        receipt.Kind == entry.Kind &&
        TenantTerminationReplayProof.FixedTimeSha256Equals(
            receipt.LogicalEntryId,
            entry.LogicalEntryId) &&
        receipt.Cursor is not null &&
        receipt.Cursor.Sequence > 0 &&
        TenantTerminationReplayProof.IsSha256(
            receipt.Cursor.RecordSha256) &&
        receipt.FlushedAtUtc != default &&
        TenantTerminationReplayProof.IsSha256(
            receipt.DurabilityProofSha256);
}
