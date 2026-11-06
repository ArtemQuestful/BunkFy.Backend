namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class DataRightsCorrectionExecution : ScopedAggregateRoot<Guid>
{
    public const int CurrentContractVersion = 2;
    public const int FieldPolicyKeyMaxLength = 120;
    public const int DigestLength = 64;
    public static readonly TimeSpan MaximumClaimLifetime = TimeSpan.FromMinutes(15);

    private DataRightsCorrectionExecution() { }

    private DataRightsCorrectionExecution(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public DataRightsCaseKind CaseKind { get; private set; }
    public Guid? PropertyId { get; private set; }
    public Guid CaseId { get; private set; }
    public long SelectedCaseVersion { get; private set; }
    public long ExecutionRevision { get; private set; }
    public long ApprovalRevision { get; private set; }
    public string OwnerKey { get; private set; } = string.Empty;
    public string RecordType { get; private set; } = string.Empty;
    public Guid RecordId { get; private set; }
    public long SelectedRecordVersion { get; private set; }
    public string FieldPolicyKey { get; private set; } = string.Empty;
    public string ExecutedBy { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DataRightsCorrectionExecutionState State { get; private set; }
    public int? ReceiptContractVersion { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public long? CurrentRecordVersion { get; private set; }
    public int? ChangedFieldCount { get; private set; }
    public string? ChangedFieldsSha256 { get; private set; }
    public string? ReceiptSha256 { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<DataRightsCorrectionExecution> Create(
        Guid executionId,
        string tenantId,
        DataRightsCaseKind caseKind,
        Guid? propertyId,
        Guid caseId,
        long selectedCaseVersion,
        long executionRevision,
        long approvalRevision,
        DataRightsSubjectCoordinate subject,
        string fieldPolicyKey,
        string executedBy,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);

        string policy = NormalizeFieldPolicy(fieldPolicyKey);
        string actor = executedBy?.Trim() ?? string.Empty;
        if (executionId == Guid.Empty ||
            !IsScopeValid(caseKind, propertyId) ||
            caseId == Guid.Empty ||
            selectedCaseVersion < 1 ||
            executionRevision != selectedCaseVersion + 1 ||
            approvalRevision < 1 ||
            subject.RecordId == Guid.Empty ||
            subject.RecordVersion < 1 ||
            policy.Length == 0 ||
            actor.Length is 0 or > DataRightsCase.ActorIdMaxLength ||
            startedAtUtc == default ||
            expiresAtUtc <= startedAtUtc ||
            expiresAtUtc - startedAtUtc > MaximumClaimLifetime ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<DataRightsCorrectionExecution>(
                DataRightsDomainErrors.CorrectionExecutionCoordinateInvalid);
        }

        return Result.Success(new DataRightsCorrectionExecution(executionId, scopeId)
        {
            ContractVersion = CurrentContractVersion,
            CaseKind = caseKind,
            PropertyId = propertyId,
            CaseId = caseId,
            SelectedCaseVersion = selectedCaseVersion,
            ExecutionRevision = executionRevision,
            ApprovalRevision = approvalRevision,
            OwnerKey = subject.OwnerKey,
            RecordType = subject.RecordType,
            RecordId = subject.RecordId,
            SelectedRecordVersion = subject.RecordVersion,
            FieldPolicyKey = policy,
            ExecutedBy = actor,
            StartedAtUtc = startedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            State = DataRightsCorrectionExecutionState.Claimed
        });
    }

    public bool MatchesClaim(
        Guid executionId,
        DataRightsCaseKind caseKind,
        Guid? propertyId,
        Guid caseId,
        long selectedCaseVersion,
        long approvalRevision,
        DataRightsSubjectCoordinate subject,
        string fieldPolicyKey,
        string executedBy) =>
        this.MatchesClaimCoordinates(
            executionId,
            caseKind,
            propertyId,
            caseId,
            selectedCaseVersion,
            approvalRevision,
            subject,
            fieldPolicyKey) &&
        this.IsOwnedBy(executedBy);

    public bool MatchesClaimCoordinates(
        Guid executionId,
        DataRightsCaseKind caseKind,
        Guid? propertyId,
        Guid caseId,
        long selectedCaseVersion,
        long approvalRevision,
        DataRightsSubjectCoordinate subject,
        string fieldPolicyKey) =>
        this.Id == executionId &&
        this.CaseKind == caseKind &&
        this.PropertyId == propertyId &&
        this.CaseId == caseId &&
        this.SelectedCaseVersion == selectedCaseVersion &&
        this.ApprovalRevision == approvalRevision &&
        string.Equals(this.OwnerKey, subject.OwnerKey, StringComparison.Ordinal) &&
        string.Equals(this.RecordType, subject.RecordType, StringComparison.Ordinal) &&
        this.RecordId == subject.RecordId &&
        this.SelectedRecordVersion == subject.RecordVersion &&
        string.Equals(
            this.FieldPolicyKey,
            NormalizeFieldPolicy(fieldPolicyKey),
            StringComparison.Ordinal);

    public bool IsOwnedBy(string executedBy) =>
        string.Equals(this.ExecutedBy, executedBy?.Trim(), StringComparison.Ordinal);

    public bool MatchesAuthorization(
        DataRightsCaseKind caseKind,
        Guid? propertyId,
        Guid caseId,
        long approvalRevision,
        Guid executionId,
        string ownerKey,
        string recordType,
        Guid recordId,
        long recordVersion,
        string fieldPolicyKey,
        string executedBy) =>
        this.Id == executionId &&
        this.CaseKind == caseKind &&
        this.PropertyId == propertyId &&
        this.CaseId == caseId &&
        this.ApprovalRevision == approvalRevision &&
        string.Equals(this.OwnerKey, ownerKey?.Trim().ToLowerInvariant(), StringComparison.Ordinal) &&
        string.Equals(
            this.RecordType,
            recordType?.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        this.RecordId == recordId &&
        this.SelectedRecordVersion == recordVersion &&
        string.Equals(
            this.FieldPolicyKey,
            NormalizeFieldPolicy(fieldPolicyKey),
            StringComparison.Ordinal) &&
        string.Equals(this.ExecutedBy, executedBy?.Trim(), StringComparison.Ordinal);

    public bool MatchesCompletion(
        Guid executionId,
        DataRightsCaseKind caseKind,
        Guid? propertyId,
        Guid caseId,
        long approvalRevision,
        string ownerKey,
        string recordType,
        Guid recordId,
        long selectedRecordVersion,
        string fieldPolicyKey) =>
        this.Id == executionId &&
        this.CaseKind == caseKind &&
        this.PropertyId == propertyId &&
        this.CaseId == caseId &&
        this.ApprovalRevision == approvalRevision &&
        string.Equals(this.OwnerKey, ownerKey?.Trim().ToLowerInvariant(), StringComparison.Ordinal) &&
        string.Equals(
            this.RecordType,
            recordType?.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        this.RecordId == recordId &&
        this.SelectedRecordVersion == selectedRecordVersion &&
        string.Equals(
            this.FieldPolicyKey,
            NormalizeFieldPolicy(fieldPolicyKey),
            StringComparison.Ordinal);

    public Result Renew(
        string executedBy,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        string actor = executedBy?.Trim() ?? string.Empty;
        if (this.State != DataRightsCorrectionExecutionState.Claimed ||
            actor.Length is 0 or > DataRightsCase.ActorIdMaxLength)
        {
            return Result.Failure(DataRightsDomainErrors.CorrectionExecutionConflict);
        }

        if (nowUtc < this.ExpiresAtUtc)
        {
            return this.IsOwnedBy(actor)
                ? Result.Success()
                : Result.Failure(DataRightsDomainErrors.CorrectionExecutionConflict);
        }

        if (expiresAtUtc <= nowUtc ||
            expiresAtUtc - nowUtc > MaximumClaimLifetime)
        {
            return Result.Failure(DataRightsDomainErrors.CorrectionExecutionCoordinateInvalid);
        }

        this.ExecutedBy = actor;
        this.StartedAtUtc = nowUtc;
        this.ExpiresAtUtc = expiresAtUtc;
        this.Version++;
        return Result.Success();
    }

    public Result Complete(
        long expectedVersion,
        int receiptContractVersion,
        Guid receiptId,
        long currentRecordVersion,
        int changedFieldCount,
        string changedFieldsSha256,
        string receiptSha256,
        DateTimeOffset completedAtUtc)
    {
        string fieldsDigest = NormalizeDigest(changedFieldsSha256);
        string receiptDigest = NormalizeDigest(receiptSha256);
        if (this.State == DataRightsCorrectionExecutionState.Completed)
        {
            return this.ReceiptContractVersion == receiptContractVersion &&
                this.ReceiptId == receiptId &&
                this.CurrentRecordVersion == currentRecordVersion &&
                this.ChangedFieldCount == changedFieldCount &&
                string.Equals(
                    this.ChangedFieldsSha256,
                    fieldsDigest,
                    StringComparison.Ordinal) &&
                string.Equals(this.ReceiptSha256, receiptDigest, StringComparison.Ordinal) &&
                this.CompletedAtUtc == completedAtUtc
                ? Result.Success()
                : Result.Failure(DataRightsDomainErrors.CorrectionExecutionConflict);
        }

        if (expectedVersion != this.Version ||
            receiptContractVersion < 1 ||
            receiptId == Guid.Empty ||
            currentRecordVersion != this.SelectedRecordVersion + 1 ||
            changedFieldCount is <= 0 or > 32 ||
            fieldsDigest.Length != DigestLength ||
            receiptDigest.Length != DigestLength ||
            completedAtUtc == default)
        {
            return Result.Failure(DataRightsDomainErrors.CorrectionExecutionProofInvalid);
        }

        this.ReceiptContractVersion = receiptContractVersion;
        this.ReceiptId = receiptId;
        this.CurrentRecordVersion = currentRecordVersion;
        this.ChangedFieldCount = changedFieldCount;
        this.ChangedFieldsSha256 = fieldsDigest;
        this.ReceiptSha256 = receiptDigest;
        this.CompletedAtUtc = completedAtUtc;
        this.State = DataRightsCorrectionExecutionState.Completed;
        this.Version++;
        return Result.Success();
    }

    private static string NormalizeFieldPolicy(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= FieldPolicyKeyMaxLength &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-')
            ? normalized
            : string.Empty;
    }

    private static string NormalizeDigest(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == DigestLength && normalized.All(Uri.IsHexDigit)
            ? normalized
            : string.Empty;
    }

    private static bool IsScopeValid(
        DataRightsCaseKind caseKind,
        Guid? propertyId) =>
        caseKind switch
        {
            DataRightsCaseKind.GuestRights =>
                propertyId is Guid value && value != Guid.Empty,
            DataRightsCaseKind.StaffRights or DataRightsCaseKind.TenantTermination =>
                propertyId is null,
            _ => false
        };
}
