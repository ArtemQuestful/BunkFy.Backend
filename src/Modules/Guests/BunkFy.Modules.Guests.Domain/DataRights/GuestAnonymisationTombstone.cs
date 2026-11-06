namespace BunkFy.Modules.Guests.Domain.DataRights;

using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestAnonymisationTombstone : ScopedAggregateRoot<Guid>
{
    public const int CurrentContractVersion = 2;
    public const long MaximumRevision = 2;

    private GuestAnonymisationTombstone() { }

    private GuestAnonymisationTombstone(Guid guestId, string scopeId)
        : base(guestId, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public long Revision { get; private set; }
    public GuestAnonymisationTombstoneState State { get; private set; }
    public GuestAnonymisationAuthority Authority { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public Guid? LedgerEntryId { get; private set; }
    public string OwnerReceiptSha256 { get; private set; } = string.Empty;
    public DateTimeOffset? LastReplayedAtUtc { get; private set; }

    public static Result<GuestAnonymisationTombstone> Create(
        string tenantId,
        Guid guestId,
        DateTimeOffset completedAtUtc,
        string ownerReceiptSha256) =>
        Create(
            tenantId,
            guestId,
            completedAtUtc,
            ownerReceiptSha256,
            GuestAnonymisationAuthority.DataRights);

    public static Result<GuestAnonymisationTombstone> CreateForRetention(
        string tenantId,
        GuestRetentionAnonymisationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(
                tenantId,
                receipt.ScopeId,
                StringComparison.Ordinal))
        {
            return Result.Failure<GuestAnonymisationTombstone>(
                GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        return Create(
            tenantId,
            receipt.GuestId,
            receipt.CompletedAtUtc,
            receipt.CanonicalSha256,
            GuestAnonymisationAuthority.Retention);
    }

    private static Result<GuestAnonymisationTombstone> Create(
        string tenantId,
        Guid guestId,
        DateTimeOffset completedAtUtc,
        string ownerReceiptSha256,
        GuestAnonymisationAuthority authority)
    {
        string receiptDigest = ownerReceiptSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (guestId == Guid.Empty ||
            completedAtUtc == default ||
            receiptDigest.Length != GuestAnonymisationReceipt.Sha256Length ||
            receiptDigest.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            authority is not (
                GuestAnonymisationAuthority.DataRights or
                GuestAnonymisationAuthority.Retention) ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<GuestAnonymisationTombstone>(
                GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        return Result.Success(new GuestAnonymisationTombstone(guestId, scopeId)
        {
            ContractVersion = CurrentContractVersion,
            Revision = 1,
            State = GuestAnonymisationTombstoneState.Anonymised,
            Authority = authority,
            CompletedAtUtc = completedAtUtc,
            OwnerReceiptSha256 = receiptDigest
        });
    }

    public static Result<GuestAnonymisationTombstone> Restore(
        string tenantId,
        Guid guestId,
        DateTimeOffset originallyCompletedAtUtc,
        string ownerReceiptSha256,
        Guid ledgerEntryId,
        DateTimeOffset replayedAtUtc)
    {
        Result<GuestAnonymisationTombstone> created = Create(
            tenantId,
            guestId,
            originallyCompletedAtUtc.ToUniversalTime(),
            ownerReceiptSha256);
        if (created.IsFailure)
        {
            return created;
        }

        if (ledgerEntryId == Guid.Empty ||
            replayedAtUtc == default ||
            replayedAtUtc.ToUniversalTime() <
                originallyCompletedAtUtc.ToUniversalTime())
        {
            return Result.Failure<GuestAnonymisationTombstone>(
                GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        created.Value.LedgerEntryId = ledgerEntryId;
        created.Value.LastReplayedAtUtc =
            replayedAtUtc.ToUniversalTime();
        return created;
    }

    public Result AttachRestoreProof(
        Guid ledgerEntryId,
        DateTimeOffset originallyCompletedAtUtc,
        string ownerReceiptSha256,
        DateTimeOffset replayedAtUtc)
    {
        string receiptDigest =
            ownerReceiptSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        DateTimeOffset completedAtUtc =
            originallyCompletedAtUtc.ToUniversalTime();
        DateTimeOffset timestamp = replayedAtUtc.ToUniversalTime();
        if (ledgerEntryId == Guid.Empty ||
            timestamp == default ||
            timestamp < completedAtUtc ||
            this.ContractVersion != CurrentContractVersion ||
            this.State != GuestAnonymisationTombstoneState.Anonymised ||
            this.Authority != GuestAnonymisationAuthority.DataRights ||
            this.CompletedAtUtc != completedAtUtc ||
            !string.Equals(
                this.OwnerReceiptSha256,
                receiptDigest,
                StringComparison.Ordinal))
        {
            return Result.Failure(
                GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        if (this.LedgerEntryId.HasValue)
        {
            return this.Revision is >= 1 and <= MaximumRevision &&
                this.LedgerEntryId == ledgerEntryId &&
                this.LastReplayedAtUtc.HasValue
                ? Result.Success()
                : Result.Failure(
                    GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        if (this.LastReplayedAtUtc.HasValue || this.Revision != 1)
        {
            return Result.Failure(
                GuestsDomainErrors.AnonymisationTombstoneInvalid);
        }

        this.LedgerEntryId = ledgerEntryId;
        this.LastReplayedAtUtc = timestamp;
        this.Revision++;
        return Result.Success();
    }

    public bool MatchesRestore(
        Guid guestId,
        Guid ledgerEntryId,
        DateTimeOffset originallyCompletedAtUtc,
        string ownerReceiptSha256) =>
        this.ContractVersion == CurrentContractVersion &&
        this.Revision is >= 1 and <= MaximumRevision &&
        this.State == GuestAnonymisationTombstoneState.Anonymised &&
        this.Authority == GuestAnonymisationAuthority.DataRights &&
        this.Id == guestId &&
        this.LedgerEntryId == ledgerEntryId &&
        this.CompletedAtUtc ==
            originallyCompletedAtUtc.ToUniversalTime() &&
        string.Equals(
            this.OwnerReceiptSha256,
            ownerReceiptSha256?.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        this.LastReplayedAtUtc.HasValue;

    public bool Matches(GuestAnonymisationReceipt receipt) =>
        receipt is not null &&
        this.ContractVersion == CurrentContractVersion &&
        this.Revision is >= 1 and <= MaximumRevision &&
        this.State == GuestAnonymisationTombstoneState.Anonymised &&
        this.Authority == GuestAnonymisationAuthority.DataRights &&
        this.Id == receipt.GuestId &&
        this.CompletedAtUtc == receipt.CompletedAtUtc &&
        string.Equals(
            this.OwnerReceiptSha256,
            receipt.CanonicalSha256,
            StringComparison.Ordinal);

    public bool MatchesRetention(
        GuestRetentionAnonymisationReceipt receipt) =>
        receipt is not null &&
        this.ContractVersion == CurrentContractVersion &&
        this.Revision == 1 &&
        this.State == GuestAnonymisationTombstoneState.Anonymised &&
        this.Authority == GuestAnonymisationAuthority.Retention &&
        !this.LedgerEntryId.HasValue &&
        !this.LastReplayedAtUtc.HasValue &&
        this.Id == receipt.GuestId &&
        this.CompletedAtUtc == receipt.CompletedAtUtc &&
        string.Equals(
            this.OwnerReceiptSha256,
            receipt.CanonicalSha256,
            StringComparison.Ordinal);
}
