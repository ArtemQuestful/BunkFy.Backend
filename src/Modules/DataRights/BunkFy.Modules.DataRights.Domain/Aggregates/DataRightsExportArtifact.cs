namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class DataRightsExportArtifact : ScopedAggregateRoot<Guid>
{
    public const int ActorIdMaxLength = DataRightsCase.ActorIdMaxLength;
    public const int FailureCodeMaxLength = 100;
    public const int StorageKeyMaxLength = 500;
    public const int Sha256Length = 64;

    private DataRightsExportArtifact() { }

    private DataRightsExportArtifact(Guid id, string scopeId) : base(id, scopeId) { }

    public Guid IdempotencyKey { get; private set; }
    public Guid CaseId { get; private set; }
    public Guid? PropertyId { get; private set; }
    public DataRightsCaseKind CaseKind { get; private set; }
    public long DecisionRevision { get; private set; }
    public int SelectedSubjectCount { get; private set; }
    public string SelectionSha256 { get; private set; } = string.Empty;
    public DataRightsExportArtifactState State { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public string? GenerationActor { get; private set; }
    public Guid? GenerationRunId { get; private set; }
    public int? GenerationAttempt { get; private set; }
    public DateTimeOffset? GenerationStartedAtUtc { get; private set; }
    public string? FailureCode { get; private set; }
    public long? LastRetryBaseVersion { get; private set; }
    public string? StorageKey { get; private set; }
    public long? EncryptedByteLength { get; private set; }
    public string? PlaintextSha256 { get; private set; }
    public int? EncryptionKeyVersion { get; private set; }
    public int? FormatVersion { get; private set; }
    public DateTimeOffset? AvailableAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public Guid? DeletionRunId { get; private set; }
    public DateTimeOffset? DeletionStartedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<DataRightsExportArtifact> Request(
        Guid id,
        string tenantId,
        Guid idempotencyKey,
        Guid caseId,
        Guid? propertyId,
        DataRightsCaseKind caseKind,
        long decisionRevision,
        int selectedSubjectCount,
        string selectionSha256,
        string actorId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty ||
            idempotencyKey == Guid.Empty ||
            caseId == Guid.Empty ||
            decisionRevision <= 0 ||
            selectedSubjectCount is <= 0 or > DataRightsCase.MaxSelectedSubjects ||
            !IsSupportedScope(caseKind, propertyId) ||
            !IsSha256(selectionSha256))
        {
            return Result.Failure<DataRightsExportArtifact>(
                DataRightsDomainErrors.ExportArtifactCoordinateInvalid);
        }

        if (!TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<DataRightsExportArtifact>(
                DataRightsDomainErrors.TenantInvalid);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure<DataRightsExportArtifact>(actor.Error);
        }

        if (nowUtc == default || expiresAtUtc <= nowUtc)
        {
            return Result.Failure<DataRightsExportArtifact>(
                DataRightsDomainErrors.TimestampInvalid);
        }

        return Result.Success(new DataRightsExportArtifact(id, scopeId)
        {
            IdempotencyKey = idempotencyKey,
            CaseId = caseId,
            PropertyId = propertyId,
            CaseKind = caseKind,
            DecisionRevision = decisionRevision,
            SelectedSubjectCount = selectedSubjectCount,
            SelectionSha256 = selectionSha256.ToLowerInvariant(),
            State = DataRightsExportArtifactState.Requested,
            RequestedBy = actor.Value,
            RequestedAtUtc = nowUtc,
            ExpiresAtUtc = expiresAtUtc
        });
    }

    public bool Matches(
        Guid idempotencyKey,
        Guid caseId,
        long decisionRevision,
        string selectionSha256) =>
        idempotencyKey != Guid.Empty &&
        this.IdempotencyKey == idempotencyKey &&
        this.MatchesCaseSnapshot(caseId, decisionRevision, selectionSha256);

    public bool MatchesCaseSnapshot(
        Guid caseId,
        long decisionRevision,
        string selectionSha256) =>
        caseId != Guid.Empty &&
        decisionRevision > 0 &&
        IsSha256(selectionSha256) &&
        this.CaseId == caseId &&
        this.DecisionRevision == decisionRevision &&
        string.Equals(
            this.SelectionSha256,
            selectionSha256,
            StringComparison.OrdinalIgnoreCase);

    public Result BeginGeneration(
        Guid runId,
        int attempt,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result<string> actor = NormalizeActor(actorId);
        if (runId == Guid.Empty || attempt <= 0 || actor.IsFailure)
        {
            return Result.Failure(
                actor.IsFailure
                    ? actor.Error
                    : DataRightsDomainErrors.ExportArtifactGenerationInvalid);
        }

        if (nowUtc == default ||
            nowUtc < this.RequestedAtUtc ||
            nowUtc >= this.ExpiresAtUtc)
        {
            return Result.Failure(DataRightsDomainErrors.TimestampInvalid);
        }

        if (this.State == DataRightsExportArtifactState.Available)
        {
            return Result.Success();
        }

        bool retry = this.State == DataRightsExportArtifactState.Generating &&
            this.GenerationRunId == runId &&
            this.GenerationAttempt is int currentAttempt &&
            attempt >= currentAttempt;
        if (this.State != DataRightsExportArtifactState.Requested && !retry)
        {
            return Result.Failure(
                DataRightsDomainErrors.ExportArtifactTransitionInvalid);
        }

        this.State = DataRightsExportArtifactState.Generating;
        this.GenerationActor = actor.Value;
        this.GenerationRunId = runId;
        this.GenerationAttempt = attempt;
        this.GenerationStartedAtUtc ??= nowUtc;
        this.FailureCode = null;
        this.Version++;
        return Result.Success();
    }

    public Result MarkAvailable(
        Guid runId,
        int attempt,
        string storageKey,
        long encryptedByteLength,
        string plaintextSha256,
        int encryptionKeyVersion,
        int formatVersion,
        DateTimeOffset availableAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        string normalizedStorageKey = storageKey?.Trim() ?? string.Empty;
        string normalizedPlaintextSha256 =
            plaintextSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (this.State == DataRightsExportArtifactState.Available &&
            this.GenerationRunId == runId &&
            this.GenerationAttempt == attempt)
        {
            return string.Equals(
                       this.StorageKey,
                       normalizedStorageKey,
                       StringComparison.Ordinal) &&
                   this.EncryptedByteLength == encryptedByteLength &&
                   string.Equals(
                       this.PlaintextSha256,
                       normalizedPlaintextSha256,
                       StringComparison.Ordinal) &&
                   this.EncryptionKeyVersion == encryptionKeyVersion &&
                   this.FormatVersion == formatVersion &&
                   this.AvailableAtUtc == availableAtUtc &&
                   this.ExpiresAtUtc == expiresAtUtc
                ? Result.Success()
                : Result.Failure(
                    DataRightsDomainErrors.ExportArtifactCompletionInvalid);
        }

        if (this.State != DataRightsExportArtifactState.Generating ||
            this.GenerationRunId != runId ||
            this.GenerationAttempt != attempt ||
            normalizedStorageKey.Length is 0 or > StorageKeyMaxLength ||
            encryptedByteLength <= 0 ||
            !IsSha256(normalizedPlaintextSha256) ||
            encryptionKeyVersion <= 0 ||
            formatVersion <= 0)
        {
            return Result.Failure(
                DataRightsDomainErrors.ExportArtifactCompletionInvalid);
        }

        if (this.GenerationStartedAtUtc is not DateTimeOffset startedAtUtc ||
            availableAtUtc == default ||
            availableAtUtc < startedAtUtc ||
            expiresAtUtc != this.ExpiresAtUtc ||
            expiresAtUtc <= availableAtUtc)
        {
            return Result.Failure(DataRightsDomainErrors.TimestampInvalid);
        }

        this.State = DataRightsExportArtifactState.Available;
        this.StorageKey = normalizedStorageKey;
        this.EncryptedByteLength = encryptedByteLength;
        this.PlaintextSha256 = normalizedPlaintextSha256;
        this.EncryptionKeyVersion = encryptionKeyVersion;
        this.FormatVersion = formatVersion;
        this.AvailableAtUtc = availableAtUtc;
        this.FailureCode = null;
        this.Version++;
        return Result.Success();
    }

    public Result MarkFailed(
        Guid runId,
        int attempt,
        string failureCode,
        DateTimeOffset failedAtUtc)
    {
        string normalizedCode = failureCode?.Trim() ?? string.Empty;
        if (this.State == DataRightsExportArtifactState.Failed &&
            this.GenerationRunId == runId &&
            this.GenerationAttempt == attempt)
        {
            return string.Equals(
                this.FailureCode,
                normalizedCode,
                StringComparison.Ordinal)
                ? Result.Success()
                : Result.Failure(
                    DataRightsDomainErrors.ExportArtifactFailureInvalid);
        }

        if (this.State != DataRightsExportArtifactState.Generating ||
            this.GenerationRunId != runId ||
            this.GenerationAttempt != attempt ||
            normalizedCode.Length is 0 or > FailureCodeMaxLength)
        {
            return Result.Failure(
                DataRightsDomainErrors.ExportArtifactFailureInvalid);
        }

        if (this.GenerationStartedAtUtc is not DateTimeOffset startedAtUtc ||
            failedAtUtc == default ||
            failedAtUtc < startedAtUtc)
        {
            return Result.Failure(DataRightsDomainErrors.TimestampInvalid);
        }

        this.State = DataRightsExportArtifactState.Failed;
        this.FailureCode = normalizedCode;
        this.Version++;
        return Result.Success();
    }

    public Result RejectGeneration(
        Guid runId,
        int attempt,
        string actorId,
        string failureCode,
        DateTimeOffset failedAtUtc)
    {
        Result<string> actor = NormalizeActor(actorId);
        string normalizedCode = failureCode?.Trim() ?? string.Empty;
        if (this.State == DataRightsExportArtifactState.Failed &&
            this.GenerationRunId == runId &&
            this.GenerationAttempt == attempt)
        {
            return string.Equals(
                this.FailureCode,
                normalizedCode,
                StringComparison.Ordinal)
                ? Result.Success()
                : Result.Failure(
                    DataRightsDomainErrors.ExportArtifactFailureInvalid);
        }

        if (this.State != DataRightsExportArtifactState.Requested ||
            runId == Guid.Empty ||
            attempt <= 0 ||
            actor.IsFailure ||
            normalizedCode.Length is 0 or > FailureCodeMaxLength)
        {
            return Result.Failure(
                actor.IsFailure
                    ? actor.Error
                    : DataRightsDomainErrors.ExportArtifactFailureInvalid);
        }

        if (failedAtUtc == default ||
            failedAtUtc < this.RequestedAtUtc ||
            failedAtUtc >= this.ExpiresAtUtc)
        {
            return Result.Failure(DataRightsDomainErrors.TimestampInvalid);
        }

        this.State = DataRightsExportArtifactState.Failed;
        this.GenerationActor = actor.Value;
        this.GenerationRunId = runId;
        this.GenerationAttempt = attempt;
        this.GenerationStartedAtUtc = failedAtUtc;
        this.FailureCode = normalizedCode;
        this.Version++;
        return Result.Success();
    }

    private static bool IsSupportedScope(
        DataRightsCaseKind caseKind,
        Guid? propertyId) =>
        caseKind switch
        {
            DataRightsCaseKind.GuestRights => propertyId is not null &&
                propertyId != Guid.Empty,
            DataRightsCaseKind.StaffRights => propertyId is null,
            _ => false
        };

    private static bool IsSha256(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == Sha256Length && normalized.All(Uri.IsHexDigit);
    }

    private static Result<string> NormalizeActor(string? actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ActorIdMaxLength
            ? Result.Success(normalized)
            : Result.Failure<string>(DataRightsDomainErrors.ActorInvalid);
    }
}
