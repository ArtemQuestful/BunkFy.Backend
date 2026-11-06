namespace BunkFy.Modules.DataRights.Application.Ports;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Naming;

public interface ITenantTerminationReplayStore
{
    Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken);

    Task<TenantTerminationReplayAppendReceipt> AppendAsync(
        TenantTerminationReplayJournalEntry entry,
        CancellationToken cancellationToken);

    Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
        TenantTerminationReplayAttemptCoordinate coordinate,
        CancellationToken cancellationToken);

    Task<TenantTerminationReplayIntent?> ReadIntentAsync(
        string tenantId,
        Guid processId,
        CancellationToken cancellationToken);

    Task<TenantTerminationReplayCheckpoint> ReadTrustedCheckpointAsync(
        string tenantId,
        Guid processId,
        CancellationToken cancellationToken);

    Task<TenantTerminationReplayPage> ReadAfterAsync(
        string tenantId,
        Guid processId,
        TenantTerminationReplayCursor cursor,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record TenantTerminationReplayAttemptCoordinate(
    string TenantId,
    Guid ProcessId,
    Guid WorkItemId,
    Guid TaskRunId,
    int TaskAttempt)
{
    public bool HasValidShape() =>
        TenantIds.TryNormalize(this.TenantId, out _) &&
        this.ProcessId != Guid.Empty &&
        this.WorkItemId != Guid.Empty &&
        this.TaskRunId != Guid.Empty &&
        this.TaskAttempt > 0;
}

public sealed record TenantTerminationReplayDispatch(
    int ContractVersion,
    TenantTerminationReplayAttemptCoordinate Coordinate,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    Guid TerminationEpoch,
    TenantTerminationContributionPhase Phase,
    Guid IdempotencyKey,
    string PolicyEvidenceSha256,
    string OwnerKey,
    int OwnerContractVersion,
    int CatalogVersion,
    string CatalogSha256,
    TenantTerminationExecutionBoundary ExecutionBoundary,
    string ExecutingActorId,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset RecordedAtUtc,
    string DispatchSha256)
{
    public const int CurrentContractVersion = 1;

    public static TenantTerminationReplayDispatch Create(
        TenantTerminationContributionRequest request,
        string ownerKey,
        int catalogVersion,
        string catalogSha256,
        TenantTerminationExecutionBoundary executionBoundary,
        Guid taskRunId,
        int taskAttempt,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenantTerminationReplayDispatch dispatch = new(
            CurrentContractVersion,
            new(
                request.TenantId,
                request.ProcessId,
                request.WorkItemId,
                taskRunId,
                taskAttempt),
            request.CaseId,
            request.ApprovalRevision,
            request.OperationRevision,
            request.TerminationEpoch,
            request.Phase,
            request.IdempotencyKey,
            request.PolicyEvidenceSha256,
            ownerKey,
            request.ContractVersion,
            catalogVersion,
            catalogSha256,
            executionBoundary,
            request.ExecutingActorId,
            request.DeadlineUtc,
            recordedAtUtc,
            DispatchSha256: string.Empty);
        dispatch = dispatch with
        {
            DispatchSha256 =
                TenantTerminationReplayProof.ComputeDispatchSha256(dispatch)
        };
        return dispatch.HasValidProof()
            ? dispatch
            : throw new ArgumentException(
                "The tenant-termination replay dispatch is invalid.",
                nameof(request));
    }

    public bool HasValidProof() =>
        this.ContractVersion == CurrentContractVersion &&
        this.Coordinate is not null &&
        this.Coordinate.HasValidShape() &&
        this.CaseId != Guid.Empty &&
        this.ApprovalRevision > 0 &&
        this.OperationRevision > this.ApprovalRevision &&
        this.TerminationEpoch != Guid.Empty &&
        TenantTerminationReplayProof.IsOwnerPhase(this.Phase) &&
        this.IdempotencyKey != Guid.Empty &&
        TenantTerminationReplayProof.IsSha256(this.PolicyEvidenceSha256) &&
        TenantTerminationReplayProof.IsStableCode(
            this.OwnerKey,
            TenantTerminationContract.OwnerKeyMaxLength) &&
        this.OwnerContractVersion == TenantTerminationContract.CurrentVersion &&
        this.CatalogVersion > 0 &&
        TenantTerminationReplayProof.IsSha256(this.CatalogSha256) &&
        Enum.IsDefined(this.ExecutionBoundary) &&
        this.ExecutionBoundary != TenantTerminationExecutionBoundary.Unknown &&
        (this.ExecutionBoundary ==
            TenantTerminationExecutionBoundary.TenantScopedTask ||
         this.Phase == TenantTerminationContributionPhase.Destroy) &&
        TenantTerminationReplayProof.IsActor(this.ExecutingActorId) &&
        this.RecordedAtUtc != default &&
        this.DeadlineUtc > this.RecordedAtUtc &&
        TenantTerminationReplayProof.FixedTimeSha256Equals(
            this.DispatchSha256,
            TenantTerminationReplayProof.ComputeDispatchSha256(this));
}

public sealed record TenantTerminationReplayResult(
    int ContractVersion,
    TenantTerminationReplayAttemptCoordinate Coordinate,
    string DispatchSha256,
    TenantTerminationContributionResult Contribution,
    DateTimeOffset ProtectedAtUtc,
    string ResultSha256)
{
    public const int CurrentContractVersion = 1;

    public static TenantTerminationReplayResult Create(
        TenantTerminationReplayDispatch dispatch,
        TenantTerminationContributionResult contribution,
        DateTimeOffset protectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(contribution);
        if (!dispatch.HasValidProof())
        {
            throw new ArgumentException(
                "The replay dispatch is invalid.",
                nameof(dispatch));
        }

        TenantTerminationReplayResult result = new(
            CurrentContractVersion,
            dispatch.Coordinate,
            dispatch.DispatchSha256,
            contribution,
            protectedAtUtc,
            ResultSha256: string.Empty);
        result = result with
        {
            ResultSha256 =
                TenantTerminationReplayProof.ComputeResultSha256(result)
        };
        return result.HasValidProof(dispatch)
            ? result
            : throw new ArgumentException(
                "The tenant-termination replay result is invalid.",
                nameof(contribution));
    }

    public bool HasValidProof(TenantTerminationReplayDispatch dispatch) =>
        dispatch is not null &&
        dispatch.HasValidProof() &&
        this.ContractVersion == CurrentContractVersion &&
        this.Coordinate == dispatch.Coordinate &&
        TenantTerminationReplayProof.FixedTimeSha256Equals(
            this.DispatchSha256,
            dispatch.DispatchSha256) &&
        this.Contribution is not null &&
        TenantTerminationReplayProof.HasValidContribution(
            dispatch,
            this.Contribution,
            this.ProtectedAtUtc) &&
        TenantTerminationReplayProof.FixedTimeSha256Equals(
            this.ResultSha256,
            TenantTerminationReplayProof.ComputeResultSha256(this));
}

public sealed record TenantTerminationReplayJournalEntry(
    int ContractVersion,
    TenantTerminationReplayEntryKind Kind,
    string LogicalEntryId,
    TenantTerminationReplayDispatch? Dispatch,
    TenantTerminationReplayResult? Result,
    TenantTerminationReplayIntent? Intent)
{
    public const int CurrentContractVersion = 1;

    public static TenantTerminationReplayJournalEntry ForDispatch(
        TenantTerminationReplayDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        TenantTerminationReplayJournalEntry entry = new(
            CurrentContractVersion,
            TenantTerminationReplayEntryKind.Dispatch,
            TenantTerminationReplayProof.ComputeLogicalEntryId(
                dispatch.Coordinate,
                TenantTerminationReplayEntryKind.Dispatch),
            dispatch,
            Result: null,
            Intent: null);
        return entry.HasValidProof()
            ? entry
            : throw new ArgumentException(
                "The tenant-termination replay dispatch is invalid.",
                nameof(dispatch));
    }

    public static TenantTerminationReplayJournalEntry ForResult(
        TenantTerminationReplayDispatch dispatch,
        TenantTerminationReplayResult result)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(result);
        TenantTerminationReplayJournalEntry entry = new(
            CurrentContractVersion,
            TenantTerminationReplayEntryKind.Result,
            TenantTerminationReplayProof.ComputeLogicalEntryId(
                dispatch.Coordinate,
                TenantTerminationReplayEntryKind.Result),
            dispatch,
            result,
            Intent: null);
        return entry.HasValidProof()
            ? entry
            : throw new ArgumentException(
                "The tenant-termination replay result is invalid.",
                nameof(result));
    }

    public static TenantTerminationReplayJournalEntry ForIntent(
        TenantTerminationReplayIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        TenantTerminationReplayJournalEntry entry = new(
            CurrentContractVersion,
            TenantTerminationReplayEntryKind.Intent,
            TenantTerminationReplayProof.ComputeIntentLogicalEntryId(
                intent.TenantId,
                intent.ProcessId),
            Dispatch: null,
            Result: null,
            intent);
        return entry.HasValidProof()
            ? entry
            : throw new ArgumentException(
                "The tenant-termination replay intent is invalid.",
                nameof(intent));
    }

    public string TenantId =>
        this.Intent?.TenantId ??
        this.Dispatch?.Coordinate.TenantId ??
        string.Empty;

    public Guid ProcessId =>
        this.Intent?.ProcessId ??
        this.Dispatch?.Coordinate.ProcessId ??
        Guid.Empty;

    public bool HasValidProof()
    {
        if (this.ContractVersion != CurrentContractVersion ||
            !Enum.IsDefined(this.Kind) ||
            this.Kind == TenantTerminationReplayEntryKind.Unknown)
        {
            return false;
        }

        return this.Kind switch
        {
            TenantTerminationReplayEntryKind.Intent =>
                this.Intent is not null &&
                this.Intent.HasValidProof() &&
                this.Dispatch is null &&
                this.Result is null &&
                TenantTerminationReplayProof.FixedTimeSha256Equals(
                    this.LogicalEntryId,
                    TenantTerminationReplayProof.ComputeIntentLogicalEntryId(
                        this.Intent.TenantId,
                        this.Intent.ProcessId)),
            TenantTerminationReplayEntryKind.Dispatch =>
                this.Intent is null &&
                this.Dispatch is not null &&
                this.Dispatch.HasValidProof() &&
                this.Result is null &&
                TenantTerminationReplayProof.FixedTimeSha256Equals(
                    this.LogicalEntryId,
                    TenantTerminationReplayProof.ComputeLogicalEntryId(
                        this.Dispatch.Coordinate,
                        this.Kind)),
            TenantTerminationReplayEntryKind.Result =>
                this.Intent is null &&
                this.Dispatch is not null &&
                this.Dispatch.HasValidProof() &&
                this.Result is not null &&
                this.Result.HasValidProof(this.Dispatch) &&
                TenantTerminationReplayProof.FixedTimeSha256Equals(
                    this.LogicalEntryId,
                    TenantTerminationReplayProof.ComputeLogicalEntryId(
                        this.Dispatch.Coordinate,
                        this.Kind)),
            _ => false
        };
    }
}

public sealed record TenantTerminationReplayAttempt(
    TenantTerminationReplayDispatch Dispatch,
    TenantTerminationReplayResult? Result);

public sealed record TenantTerminationReplayCursor(
    long Sequence,
    string RecordSha256)
{
    public static readonly TenantTerminationReplayCursor Genesis = new(
        Sequence: 0,
        TenantTerminationReplayProof.GenesisSha256);
}

public sealed record TenantTerminationReplayCheckpoint(
    int ContractVersion,
    Guid ProcessId,
    TenantTerminationReplayCursor Cursor,
    int IntegrityKeyVersion,
    string CheckpointProofSha256,
    DateTimeOffset FlushedAtUtc)
{
    public const int CurrentContractVersion = 1;
}

public sealed record TenantTerminationReplayAppendReceipt(
    int ContractVersion,
    string LogicalEntryId,
    TenantTerminationReplayEntryKind Kind,
    TenantTerminationReplayCursor Cursor,
    DateTimeOffset FlushedAtUtc,
    string DurabilityProofSha256)
{
    public const int CurrentContractVersion = 1;
}

public sealed record TenantTerminationReplayPage(
    int ContractVersion,
    IReadOnlyList<TenantTerminationReplayJournalEntry> Entries,
    TenantTerminationReplayCursor NextCursor,
    bool HasMore)
{
    public const int CurrentContractVersion = 1;
}

public sealed record TenantTerminationReplayStoreReadiness(
    string Provider,
    bool IsReady,
    bool IsProductionGrade,
    string? FailureCode);

public enum TenantTerminationReplayEntryKind
{
    Unknown = 0,
    Dispatch = 1,
    Result = 2,
    Intent = 3
}

public sealed class TenantTerminationReplayStoreException : Exception
{
    public TenantTerminationReplayStoreException(string code, string message)
        : base(message) =>
        this.Code = code;

    public TenantTerminationReplayStoreException(
        string code,
        string message,
        Exception innerException)
        : base(message, innerException) =>
        this.Code = code;

    public string Code { get; }
}

public static partial class TenantTerminationReplayProof
{
    public static readonly string GenesisSha256 = new('0', 64);
    private const string DispatchDomain =
        "bunkfy.data-rights.tenant-termination.replay-dispatch.v1";
    private const string ResultDomain =
        "bunkfy.data-rights.tenant-termination.replay-result.v1";
    private const string LogicalEntryDomain =
        "bunkfy.data-rights.tenant-termination.replay-entry.v1";

    public static string ComputeDispatchSha256(
        TenantTerminationReplayDispatch dispatch)
    {
        StringBuilder canonical = new();
        Append(canonical, DispatchDomain);
        Append(canonical, dispatch.ContractVersion);
        Append(canonical, NormalizeTenant(dispatch.Coordinate.TenantId));
        Append(canonical, dispatch.Coordinate.ProcessId);
        Append(canonical, dispatch.Coordinate.WorkItemId);
        Append(canonical, dispatch.Coordinate.TaskRunId);
        Append(canonical, dispatch.Coordinate.TaskAttempt);
        Append(canonical, dispatch.CaseId);
        Append(canonical, dispatch.ApprovalRevision);
        Append(canonical, dispatch.OperationRevision);
        Append(canonical, dispatch.TerminationEpoch);
        Append(canonical, (int)dispatch.Phase);
        Append(canonical, dispatch.IdempotencyKey);
        Append(canonical, dispatch.PolicyEvidenceSha256);
        Append(canonical, dispatch.OwnerKey.Trim());
        Append(canonical, dispatch.OwnerContractVersion);
        Append(canonical, dispatch.CatalogVersion);
        Append(canonical, dispatch.CatalogSha256);
        Append(canonical, (int)dispatch.ExecutionBoundary);
        Append(canonical, dispatch.ExecutingActorId.Trim());
        Append(canonical, dispatch.DeadlineUtc);
        Append(canonical, dispatch.RecordedAtUtc);
        return Hash(canonical);
    }

    public static string ComputeResultSha256(
        TenantTerminationReplayResult result)
    {
        StringBuilder canonical = new();
        Append(canonical, ResultDomain);
        Append(canonical, result.ContractVersion);
        Append(canonical, NormalizeTenant(result.Coordinate.TenantId));
        Append(canonical, result.Coordinate.ProcessId);
        Append(canonical, result.Coordinate.WorkItemId);
        Append(canonical, result.Coordinate.TaskRunId);
        Append(canonical, result.Coordinate.TaskAttempt);
        Append(canonical, result.DispatchSha256);
        Append(canonical, (int)result.Contribution.Status);
        Append(canonical, result.Contribution.ResultCode.Trim());
        Append(canonical, result.Contribution.AffectedCount);
        Append(canonical, result.Contribution.RetainedMinimumCount);
        Append(canonical, result.Contribution.RemainingActiveCount);
        Append(canonical, result.Contribution.HoldReviewAtUtc);
        Append(canonical, result.Contribution.SelectedProofRevision);
        Append(canonical, result.Contribution.ResultingProofRevision);
        Append(canonical, result.Contribution.CatalogVersion);
        Append(canonical, result.Contribution.CatalogSha256);
        Append(canonical, result.Contribution.RecordedAtUtc);
        Append(canonical, result.ProtectedAtUtc);
        return Hash(canonical);
    }

    public static string ComputeLogicalEntryId(
        TenantTerminationReplayAttemptCoordinate coordinate,
        TenantTerminationReplayEntryKind kind)
    {
        StringBuilder canonical = new();
        Append(canonical, LogicalEntryDomain);
        Append(canonical, NormalizeTenant(coordinate.TenantId));
        Append(canonical, coordinate.ProcessId);
        Append(canonical, coordinate.WorkItemId);
        Append(canonical, coordinate.TaskRunId);
        Append(canonical, coordinate.TaskAttempt);
        Append(canonical, (int)kind);
        return Hash(canonical);
    }

    public static bool MatchesDispatch(
        TenantTerminationReplayDispatch? dispatch,
        TenantTerminationContributionRequest? request,
        string ownerKey,
        int catalogVersion,
        string catalogSha256,
        TenantTerminationExecutionBoundary executionBoundary,
        Guid taskRunId,
        int taskAttempt) =>
        dispatch is not null &&
        request is not null &&
        dispatch.HasValidProof() &&
        dispatch.Coordinate == new TenantTerminationReplayAttemptCoordinate(
            request.TenantId,
            request.ProcessId,
            request.WorkItemId,
            taskRunId,
            taskAttempt) &&
        dispatch.CaseId == request.CaseId &&
        dispatch.ApprovalRevision == request.ApprovalRevision &&
        dispatch.OperationRevision == request.OperationRevision &&
        dispatch.TerminationEpoch == request.TerminationEpoch &&
        dispatch.Phase == request.Phase &&
        dispatch.IdempotencyKey == request.IdempotencyKey &&
        FixedTimeSha256Equals(
            dispatch.PolicyEvidenceSha256,
            request.PolicyEvidenceSha256) &&
        string.Equals(
            dispatch.OwnerKey,
            ownerKey?.Trim(),
            StringComparison.Ordinal) &&
        dispatch.OwnerContractVersion == request.ContractVersion &&
        dispatch.CatalogVersion == catalogVersion &&
        FixedTimeSha256Equals(
            dispatch.CatalogSha256,
            catalogSha256) &&
        dispatch.ExecutionBoundary == executionBoundary &&
        string.Equals(
            dispatch.ExecutingActorId,
            request.ExecutingActorId?.Trim(),
            StringComparison.Ordinal) &&
        dispatch.DeadlineUtc == request.DeadlineUtc;

    public static bool HasValidContribution(
        TenantTerminationReplayDispatch dispatch,
        TenantTerminationContributionResult contribution,
        DateTimeOffset protectedAtUtc)
    {
        if (!Enum.IsDefined(contribution.Status) ||
            contribution.Status == TenantTerminationContributionStatus.Unknown ||
            !IsStableCode(
                contribution.ResultCode,
                TenantTerminationContract.ResultCodeMaxLength) ||
            contribution.AffectedCount < 0 ||
            contribution.RetainedMinimumCount < 0 ||
            contribution.RemainingActiveCount < 0 ||
            contribution.CatalogVersion != dispatch.CatalogVersion ||
            !FixedTimeSha256Equals(
                contribution.CatalogSha256,
                dispatch.CatalogSha256) ||
            contribution.RecordedAtUtc < dispatch.RecordedAtUtc ||
            contribution.RecordedAtUtc >= dispatch.DeadlineUtc ||
            protectedAtUtc < contribution.RecordedAtUtc ||
            protectedAtUtc >= dispatch.DeadlineUtc)
        {
            return false;
        }

        return contribution.Status switch
        {
            TenantTerminationContributionStatus.Completed =>
                contribution.RemainingActiveCount == 0 &&
                !contribution.HoldReviewAtUtc.HasValue &&
                contribution.SelectedProofRevision is >= 0 &&
                contribution.ResultingProofRevision >=
                    contribution.SelectedProofRevision,
            TenantTerminationContributionStatus.Blocked =>
                contribution.RemainingActiveCount > 0 &&
                (!contribution.HoldReviewAtUtc.HasValue ||
                 contribution.HoldReviewAtUtc.Value >=
                    contribution.RecordedAtUtc) &&
                !contribution.SelectedProofRevision.HasValue &&
                !contribution.ResultingProofRevision.HasValue,
            TenantTerminationContributionStatus.RetryRequired or
                TenantTerminationContributionStatus.Failed =>
                !contribution.HoldReviewAtUtc.HasValue &&
                !contribution.SelectedProofRevision.HasValue &&
                !contribution.ResultingProofRevision.HasValue,
            _ => false
        };
    }

    public static bool IsOwnerPhase(TenantTerminationContributionPhase phase) =>
        phase is TenantTerminationContributionPhase.Freeze or
            TenantTerminationContributionPhase.Export or
            TenantTerminationContributionPhase.Destroy or
            TenantTerminationContributionPhase.Restore;

    public static bool IsActor(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
                TenantTerminationContract.ActorIdMaxLength &&
            normalized.All(character => !char.IsControl(character));
    }

    public static bool IsStableCode(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximumLength &&
            normalized[0] is >= 'a' and <= 'z' &&
            normalized.All(character =>
                character is (>= 'a' and <= 'z') or
                    (>= '0' and <= '9') or '.' or '-' or '_');
    }

    public static bool IsSha256(string? value) =>
        value is { Length: TenantTerminationContract.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static bool FixedTimeSha256Equals(
        string? left,
        string? right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }

        byte[] leftBytes = Convert.FromHexString(left!);
        byte[] rightBytes = Convert.FromHexString(right!);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                leftBytes,
                rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static string NormalizeTenant(string tenantId) =>
        TenantIds.TryNormalize(tenantId, out string? normalized)
            ? normalized
            : tenantId?.Trim() ?? string.Empty;

    private static string Hash(StringBuilder canonical)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static void Append(StringBuilder target, Guid value) =>
        Append(target, value.ToString("N"));

    private static void Append(StringBuilder target, int value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder target, long value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder target, long? value) =>
        Append(
            target,
            value?.ToString(CultureInfo.InvariantCulture) ?? "-");

    private static void Append(
        StringBuilder target,
        DateTimeOffset value) =>
        Append(
            target,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void Append(
        StringBuilder target,
        DateTimeOffset? value) =>
        Append(
            target,
            value?.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture) ?? "-");
}
