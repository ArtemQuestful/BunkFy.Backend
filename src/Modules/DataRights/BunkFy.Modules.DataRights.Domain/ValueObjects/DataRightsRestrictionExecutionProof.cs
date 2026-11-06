namespace BunkFy.Modules.DataRights.Domain.ValueObjects;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

public sealed class DataRightsRestrictionExecutionProof
{
    public const int Sha256Length = 64;

    private DataRightsRestrictionExecutionProof() { }

    private DataRightsRestrictionExecutionProof(
        Guid idempotencyKey,
        long approvalRevision,
        DataRightsRestrictionAction directive,
        DataRightsSubjectCoordinate subject,
        int receiptContractVersion,
        Guid receiptId,
        Guid ownerOperationId,
        long resultingOwnerRevision,
        long resultingProjectionRevision,
        bool effectiveRestricted,
        string receiptSha256,
        string executedBy,
        DateTimeOffset completedAtUtc)
    {
        this.IdempotencyKey = idempotencyKey;
        this.ApprovalRevision = approvalRevision;
        this.Directive = directive;
        this.OwnerKey = subject.OwnerKey;
        this.RecordType = subject.RecordType;
        this.RecordId = subject.RecordId;
        this.SelectedRecordVersion = subject.RecordVersion;
        this.ReceiptContractVersion = receiptContractVersion;
        this.ReceiptId = receiptId;
        this.OwnerOperationId = ownerOperationId;
        this.ResultingOwnerRevision = resultingOwnerRevision;
        this.ResultingProjectionRevision = resultingProjectionRevision;
        this.EffectiveRestricted = effectiveRestricted;
        this.ReceiptSha256 = receiptSha256;
        this.ExecutedBy = executedBy;
        this.CompletedAtUtc = completedAtUtc;
    }

    public Guid IdempotencyKey { get; private set; }
    public long ApprovalRevision { get; private set; }
    public DataRightsRestrictionAction Directive { get; private set; }
    public string OwnerKey { get; private set; } = string.Empty;
    public string RecordType { get; private set; } = string.Empty;
    public Guid RecordId { get; private set; }
    public long SelectedRecordVersion { get; private set; }
    public int ReceiptContractVersion { get; private set; }
    public Guid ReceiptId { get; private set; }
    public Guid OwnerOperationId { get; private set; }
    public long ResultingOwnerRevision { get; private set; }
    public long ResultingProjectionRevision { get; private set; }
    public bool EffectiveRestricted { get; private set; }
    public string ReceiptSha256 { get; private set; } = string.Empty;
    public string ExecutedBy { get; private set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; private set; }

    public static Result<DataRightsRestrictionExecutionProof> Create(
        Guid idempotencyKey,
        long approvalRevision,
        DataRightsRestrictionAction directive,
        DataRightsSubjectCoordinate subject,
        int receiptContractVersion,
        Guid receiptId,
        Guid ownerOperationId,
        long resultingOwnerRevision,
        long resultingProjectionRevision,
        bool effectiveRestricted,
        string receiptSha256,
        string executedBy,
        DateTimeOffset completedAtUtc,
        DataRightsRestrictionReleaseTarget? releaseTarget = null)
    {
        ArgumentNullException.ThrowIfNull(subject);

        string digest = receiptSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        string actor = executedBy?.Trim() ?? string.Empty;
        bool effectiveStateMatches = directive switch
        {
            DataRightsRestrictionAction.Apply =>
                releaseTarget is null && effectiveRestricted,
            DataRightsRestrictionAction.Release when releaseTarget is null =>
                !effectiveRestricted,
            DataRightsRestrictionAction.Release =>
                ownerOperationId == releaseTarget.OwnerOperationId &&
                resultingOwnerRevision == releaseTarget.OwnerOperationVersion + 1,
            _ => false
        };
        if (idempotencyKey == Guid.Empty ||
            approvalRevision < 1 ||
            receiptContractVersion < 1 ||
            receiptId == Guid.Empty ||
            ownerOperationId == Guid.Empty ||
            resultingOwnerRevision < 1 ||
            resultingProjectionRevision < 1 ||
            !effectiveStateMatches ||
            digest.Length != Sha256Length ||
            !digest.All(Uri.IsHexDigit) ||
            actor.Length is 0 or > DataRightsCase.ActorIdMaxLength ||
            completedAtUtc == default)
        {
            return Result.Failure<DataRightsRestrictionExecutionProof>(
                DataRightsDomainErrors.RestrictionExecutionProofInvalid);
        }

        return Result.Success(new DataRightsRestrictionExecutionProof(
            idempotencyKey,
            approvalRevision,
            directive,
            subject,
            receiptContractVersion,
            receiptId,
            ownerOperationId,
            resultingOwnerRevision,
            resultingProjectionRevision,
            effectiveRestricted,
            digest,
            actor,
            completedAtUtc));
    }

    public bool Matches(
        Guid idempotencyKey,
        long approvalRevision,
        DataRightsRestrictionAction directive,
        DataRightsSubjectCoordinate subject,
        string actorId,
        DataRightsRestrictionReleaseTarget? releaseTarget = null) =>
        this.IdempotencyKey == idempotencyKey &&
        this.ApprovalRevision == approvalRevision &&
        this.Directive == directive &&
        string.Equals(this.OwnerKey, subject.OwnerKey, StringComparison.Ordinal) &&
        string.Equals(this.RecordType, subject.RecordType, StringComparison.Ordinal) &&
        this.RecordId == subject.RecordId &&
        this.SelectedRecordVersion == subject.RecordVersion &&
        this.MatchesReleaseTarget(directive, releaseTarget) &&
        string.Equals(this.ExecutedBy, actorId?.Trim(), StringComparison.Ordinal);

    private bool MatchesReleaseTarget(
        DataRightsRestrictionAction directive,
        DataRightsRestrictionReleaseTarget? releaseTarget) =>
        directive switch
        {
            DataRightsRestrictionAction.Apply => releaseTarget is null,
            DataRightsRestrictionAction.Release when releaseTarget is null =>
                !this.EffectiveRestricted,
            DataRightsRestrictionAction.Release =>
                this.OwnerOperationId == releaseTarget.OwnerOperationId &&
                this.ResultingOwnerRevision ==
                    releaseTarget.OwnerOperationVersion + 1,
            _ => false
        };
}
