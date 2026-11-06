namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class TenantTerminationProcess : ScopedAggregateRoot<Guid>
{
    public const int ActorIdMaxLength = 200;
    public const int OutcomeCodeMaxLength = 200;
    public const int Sha256Length = 64;
    public const int MaximumFrozenOwners = 64;
    public const string ExportArtifactExpiredOutcomeCode =
        "export-artifact-expired";
    public const string ExportFragmentExpiredOutcomeCode =
        "export-fragment-expired";

    private readonly List<TenantTerminationFrozenOwner> frozenExportOwners = [];

    private TenantTerminationProcess() { }

    private TenantTerminationProcess(Guid id, string scopeId) : base(id, scopeId) { }

    public Guid IdempotencyKey { get; private set; }
    public Guid CaseId { get; private set; }
    public long ApprovalRevision { get; private set; }
    public Guid TerminationEpoch { get; private set; }
    public bool ExportRequested { get; private set; }
    public string PolicyEvidenceSha256 { get; private set; } = string.Empty;
    public string ApprovedBy { get; private set; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; private set; }
    public TenantTerminationProcessPhase Phase { get; private set; }
    public TenantTerminationProcessStatus Status { get; private set; }
    public long OperationRevision { get; private set; }
    public string? OutcomeCode { get; private set; }
    public DateTimeOffset? HoldReviewAtUtc { get; private set; }
    public long? FreezeOperationRevision { get; private set; }
    public long? WorkspaceFenceRevision { get; private set; }
    public string? FrozenRevisionSha256 { get; private set; }
    public string? FrozenBy { get; private set; }
    public DateTimeOffset? FrozenAtUtc { get; private set; }
    public long ExportConfirmationRevision { get; private set; }
    public long? ExportConfirmedOperationRevision { get; private set; }
    public Guid? ExportArtifactId { get; private set; }
    public long? ExportArtifactVersion { get; private set; }
    public string? ExportFrozenRevisionSha256 { get; private set; }
    public string? ExportFragmentSetSha256 { get; private set; }
    public string? ExportConfirmedBy { get; private set; }
    public DateTimeOffset? ExportConfirmedAtUtc { get; private set; }
    public long? DestroyCompletedOperationRevision { get; private set; }
    public DateTimeOffset? DestroyedAtUtc { get; private set; }
    public long VerificationConfirmationRevision { get; private set; }
    public long? VerificationConfirmedOperationRevision { get; private set; }
    public Guid? TerminalReceiptId { get; private set; }
    public long? TerminalReceiptVersion { get; private set; }
    public string? VerificationOwnerProofSetSha256 { get; private set; }
    public string? VerificationConfirmedBy { get; private set; }
    public DateTimeOffset? VerificationConfirmedAtUtc { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string LastChangedBy { get; private set; } = string.Empty;
    public DateTimeOffset LastChangedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;
    public IReadOnlyCollection<TenantTerminationFrozenOwner>
        FrozenExportOwners => this.frozenExportOwners.AsReadOnly();

    public static Result<TenantTerminationProcess> Prepare(
        Guid id,
        string tenantId,
        Guid idempotencyKey,
        Guid caseId,
        long approvalRevision,
        Guid terminationEpoch,
        bool exportRequested,
        string policyEvidenceSha256,
        string approvedBy,
        DateTimeOffset approvedAtUtc,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty ||
            idempotencyKey == Guid.Empty ||
            caseId == Guid.Empty ||
            approvalRevision is <= 0 or long.MaxValue ||
            terminationEpoch == Guid.Empty ||
            !IsSha256(policyEvidenceSha256))
        {
            return Result.Failure<TenantTerminationProcess>(
                DataRightsDomainErrors.TenantTerminationCoordinateInvalid);
        }

        if (!TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<TenantTerminationProcess>(
                DataRightsDomainErrors.TenantInvalid);
        }

        string normalizedApprover = NormalizeActor(approvedBy);
        string normalizedActor = NormalizeActor(actorId);
        if (normalizedApprover.Length == 0 ||
            normalizedActor.Length == 0 ||
            approvedAtUtc == default ||
            nowUtc == default ||
            approvedAtUtc > nowUtc)
        {
            return Result.Failure<TenantTerminationProcess>(
                DataRightsDomainErrors.TenantTerminationCoordinateInvalid);
        }

        return Result.Success(new TenantTerminationProcess(id, scopeId)
        {
            IdempotencyKey = idempotencyKey,
            CaseId = caseId,
            ApprovalRevision = approvalRevision,
            TerminationEpoch = terminationEpoch,
            ExportRequested = exportRequested,
            PolicyEvidenceSha256 = policyEvidenceSha256,
            ApprovedBy = normalizedApprover,
            ApprovedAtUtc = approvedAtUtc,
            Phase = TenantTerminationProcessPhase.Freeze,
            Status = TenantTerminationProcessStatus.Pending,
            OperationRevision = approvalRevision,
            CreatedBy = normalizedActor,
            CreatedAtUtc = nowUtc,
            LastChangedBy = normalizedActor,
            LastChangedAtUtc = nowUtc
        });
    }

    public bool Matches(
        Guid idempotencyKey,
        Guid caseId,
        long approvalRevision) =>
        idempotencyKey != Guid.Empty &&
        caseId != Guid.Empty &&
        approvalRevision > 0 &&
        this.IdempotencyKey == idempotencyKey &&
        this.CaseId == caseId &&
        this.ApprovalRevision == approvalRevision;

    private static string NormalizeActor(string? actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ActorIdMaxLength
            ? normalized
            : string.Empty;
    }

    internal static bool IsStableCode(string? value, int maxLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0 &&
            normalized.Length <= maxLength &&
            normalized[0] is >= 'a' and <= 'z' &&
            normalized.All(character =>
                character is (>= 'a' and <= 'z') or
                    (>= '0' and <= '9') or '.' or '-' or '_');
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
