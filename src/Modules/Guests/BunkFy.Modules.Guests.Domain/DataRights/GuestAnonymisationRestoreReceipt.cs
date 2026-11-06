namespace BunkFy.Modules.Guests.Domain.DataRights;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestAnonymisationRestoreReceipt : ScopedEntity<Guid>
{
    public const int CurrentContractVersion = 1;

    private GuestAnonymisationRestoreReceipt() { }

    private GuestAnonymisationRestoreReceipt(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public Guid LedgerEntryId { get; private set; }
    public Guid GuestId { get; private set; }
    public int OwnerReceiptContractVersion { get; private set; }
    public Guid OwnerReceiptId { get; private set; }
    public string OwnerReceiptSha256 { get; private set; } = string.Empty;
    public long ResultingGuestVersion { get; private set; }
    public long TombstoneRevision { get; private set; }
    public DateTimeOffset ReplayedAtUtc { get; private set; }
    public string CanonicalSha256 { get; private set; } = string.Empty;

    public static Result<GuestAnonymisationRestoreReceipt> Create(
        string tenantId,
        Guid ledgerEntryId,
        Guid guestId,
        int ownerReceiptContractVersion,
        Guid ownerReceiptId,
        string ownerReceiptSha256,
        long resultingGuestVersion,
        long tombstoneRevision,
        DateTimeOffset replayedAtUtc)
    {
        string receiptSha256 =
            ownerReceiptSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        DateTimeOffset timestamp = replayedAtUtc.ToUniversalTime();
        if (!TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            ledgerEntryId == Guid.Empty ||
            guestId == Guid.Empty ||
            ownerReceiptContractVersion <= 0 ||
            ownerReceiptId == Guid.Empty ||
            !IsSha256(receiptSha256) ||
            resultingGuestVersion <= 0 ||
            tombstoneRevision is <= 0 or >
                GuestAnonymisationTombstone.MaximumRevision ||
            timestamp == default)
        {
            return Result.Failure<GuestAnonymisationRestoreReceipt>(
                GuestsDomainErrors.AnonymisationRestoreReceiptInvalid);
        }

        GuestAnonymisationRestoreReceipt receipt =
            new(ledgerEntryId, scopeId)
            {
                ContractVersion = CurrentContractVersion,
                LedgerEntryId = ledgerEntryId,
                GuestId = guestId,
                OwnerReceiptContractVersion =
                    ownerReceiptContractVersion,
                OwnerReceiptId = ownerReceiptId,
                OwnerReceiptSha256 = receiptSha256,
                ResultingGuestVersion = resultingGuestVersion,
                TombstoneRevision = tombstoneRevision,
                ReplayedAtUtc = timestamp
            };
        receipt.CanonicalSha256 = receipt.ComputeCanonicalSha256();
        return Result.Success(receipt);
    }

    public bool Matches(
        Guid ledgerEntryId,
        Guid guestId,
        int ownerReceiptContractVersion,
        Guid ownerReceiptId,
        string ownerReceiptSha256) =>
        this.ContractVersion == CurrentContractVersion &&
        this.Id == ledgerEntryId &&
        this.LedgerEntryId == ledgerEntryId &&
        this.GuestId == guestId &&
        this.OwnerReceiptContractVersion ==
            ownerReceiptContractVersion &&
        this.OwnerReceiptId == ownerReceiptId &&
        string.Equals(
            this.OwnerReceiptSha256,
            ownerReceiptSha256?.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        string.Equals(
            this.CanonicalSha256,
            this.ComputeCanonicalSha256(),
            StringComparison.Ordinal);

    private string ComputeCanonicalSha256()
    {
        StringBuilder canonical = new();
        Append(
            canonical,
            this.ContractVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, this.Id.ToString("N"));
        Append(canonical, this.ScopeId);
        Append(canonical, this.LedgerEntryId.ToString("N"));
        Append(canonical, this.GuestId.ToString("N"));
        Append(
            canonical,
            this.OwnerReceiptContractVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(canonical, this.OwnerReceiptId.ToString("N"));
        Append(canonical, this.OwnerReceiptSha256);
        Append(
            canonical,
            this.ResultingGuestVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            this.TombstoneRevision.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            this.ReplayedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(
            value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static bool IsSha256(string value) =>
        value.Length == GuestAnonymisationReceipt.Sha256Length &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
