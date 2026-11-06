namespace BunkFy.Modules.Guests.Domain.Retention;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestRetentionAnonymisationReceipt
    : ScopedAggregateRoot<Guid>
{
    public const int MinimumSupportedContractVersion = 1;
    public const int CurrentContractVersion = 2;
    public const int Sha256Length = 64;
    public const int TimeZoneCatalogVersionMaxLength = 64;
    public const int MaximumAffectedProperties = 256;
    public const string SystemActorId = "system:retention";

    private GuestRetentionAnonymisationReceipt() { }

    private GuestRetentionAnonymisationReceipt(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public Guid ExecutionId { get; private set; }
    public Guid GuestId { get; private set; }
    public long SelectedGuestVersion { get; private set; }
    public long ResultingGuestVersion { get; private set; }
    public int AffectedPropertyCount { get; private set; }
    public DateTimeOffset RetentionDeadlineUtc { get; private set; }
    public string PolicySetSha256 { get; private set; } = string.Empty;
    public string? TimeZoneCatalogVersion { get; private set; }
    public Guid EventId { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public string CanonicalSha256 { get; private set; } = string.Empty;

    public static Result<GuestRetentionAnonymisationReceipt> Create(
        Guid receiptId,
        string tenantId,
        Guid executionId,
        Guid guestId,
        long selectedGuestVersion,
        long resultingGuestVersion,
        int affectedPropertyCount,
        DateTimeOffset retentionDeadlineUtc,
        string policySetSha256,
        string timeZoneCatalogVersion,
        Guid eventId,
        string actorId,
        DateTimeOffset completedAtUtc)
    {
        string policyDigest = NormalizeSha256(policySetSha256);
        string catalogVersion =
            timeZoneCatalogVersion?.Trim() ?? string.Empty;
        string actor = actorId?.Trim() ?? string.Empty;
        if (receiptId == Guid.Empty ||
            executionId == Guid.Empty ||
            guestId == Guid.Empty ||
            eventId == Guid.Empty ||
            selectedGuestVersion < 1 ||
            resultingGuestVersion != selectedGuestVersion + 1 ||
            affectedPropertyCount is < 1 or >
                MaximumAffectedProperties ||
            retentionDeadlineUtc == default ||
            completedAtUtc < retentionDeadlineUtc ||
            !string.Equals(actor, SystemActorId, StringComparison.Ordinal) ||
            !IsSha256(policyDigest) ||
            !IsCatalogVersion(catalogVersion) ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Invalid();
        }

        GuestRetentionAnonymisationReceipt receipt =
            new(receiptId, scopeId)
            {
                ContractVersion = CurrentContractVersion,
                ExecutionId = executionId,
                GuestId = guestId,
                SelectedGuestVersion = selectedGuestVersion,
                ResultingGuestVersion = resultingGuestVersion,
                AffectedPropertyCount = affectedPropertyCount,
                RetentionDeadlineUtc =
                    retentionDeadlineUtc.ToUniversalTime(),
                PolicySetSha256 = policyDigest,
                TimeZoneCatalogVersion = catalogVersion,
                EventId = eventId,
                ActorId = actor,
                CompletedAtUtc = completedAtUtc.ToUniversalTime()
            };
        receipt.CanonicalSha256 = receipt.ComputeCanonicalSha256();
        return Result.Success(receipt);
    }

    public bool Matches(
        Guid executionId,
        Guid guestId,
        long selectedGuestVersion) =>
        this.ContractVersion is >= MinimumSupportedContractVersion and
            <= CurrentContractVersion &&
        this.HasValidVersionShape() &&
        this.ExecutionId == executionId &&
        this.GuestId == guestId &&
        this.SelectedGuestVersion == selectedGuestVersion &&
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
        Append(canonical, this.ExecutionId.ToString("N"));
        Append(canonical, this.GuestId.ToString("N"));
        Append(
            canonical,
            this.SelectedGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            this.ResultingGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            this.AffectedPropertyCount.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            this.RetentionDeadlineUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, this.PolicySetSha256);
        if (this.ContractVersion >= 2)
        {
            Append(canonical, this.TimeZoneCatalogVersion ?? string.Empty);
        }

        Append(canonical, this.EventId.ToString("N"));
        Append(canonical, this.ActorId);
        Append(
            canonical,
            this.CompletedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(
            value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static string NormalizeSha256(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool IsSha256(string value) =>
        value.Length == Sha256Length &&
        value.All(character =>
            character is (>= '0' and <= '9') or
                (>= 'a' and <= 'f'));

    private static bool IsCatalogVersion(string? value) =>
        value is not null &&
        value.Length is > 0 and <= TimeZoneCatalogVersionMaxLength &&
        value.All(character => !char.IsControl(character));

    private bool HasValidVersionShape() =>
        this.ContractVersion switch
        {
            1 => this.TimeZoneCatalogVersion is null,
            2 => IsCatalogVersion(this.TimeZoneCatalogVersion) &&
                 string.Equals(
                     this.TimeZoneCatalogVersion,
                     this.TimeZoneCatalogVersion!.Trim(),
                     StringComparison.Ordinal),
            _ => false
        };

    private static Result<GuestRetentionAnonymisationReceipt> Invalid() =>
        Result.Failure<GuestRetentionAnonymisationReceipt>(
            GuestsDomainErrors.RetentionReceiptInvalid);
}
