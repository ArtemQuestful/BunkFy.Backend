namespace BunkFy.Modules.DataRights.Contracts;

public interface IDataRightsRestrictionContributor
{
    string OwnerKey { get; }

    int ContractVersion { get; }

    Task<DataRightsRestrictionTargetResolutionResult> ResolveReleaseTargetsAsync(
        DataRightsRestrictionTargetResolutionRequest request,
        CancellationToken cancellationToken);

    Task<DataRightsRestrictionContributionResult> ExecuteAsync(
        DataRightsRestrictionContributionRequest request,
        CancellationToken cancellationToken);
}

public sealed record DataRightsRestrictionContributionRequest(
    int ContractVersion,
    string TenantId,
    Guid IdempotencyKey,
    Guid? PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    DataRightsSubjectCoordinate Coordinate,
    DataRightsRestrictionDirective Directive,
    string ExecutingActorId,
    DateTimeOffset DeadlineUtc,
    DataRightsCaseType CaseType = DataRightsCaseType.GuestRights,
    Guid? TargetOwnerOperationId = null,
    long? TargetOwnerOperationVersion = null);

public sealed record DataRightsRestrictionTargetResolutionRequest(
    int ContractVersion,
    string TenantId,
    Guid? PropertyId,
    Guid CaseId,
    DataRightsSubjectCoordinate Coordinate,
    DateTimeOffset DeadlineUtc,
    DataRightsCaseType CaseType = DataRightsCaseType.GuestRights,
    Guid? TargetOwnerOperationId = null,
    long? TargetOwnerOperationVersion = null);

public sealed record DataRightsRestrictionReleaseTarget(
    Guid OwnerOperationId,
    long OwnerOperationVersion,
    Guid SourceCaseId,
    DateTimeOffset AppliedAtUtc);

public sealed record DataRightsRestrictionTargetResolutionResult(
    int ContractVersion,
    DataRightsRestrictionTargetResolutionStatus Status,
    IReadOnlyCollection<DataRightsRestrictionReleaseTarget>? Targets,
    bool LimitReached,
    string? OutcomeCode)
{
    public static DataRightsRestrictionTargetResolutionResult Completed(
        IReadOnlyCollection<DataRightsRestrictionReleaseTarget> targets,
        bool limitReached = false) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionTargetResolutionStatus.Completed,
            targets,
            limitReached,
            OutcomeCode: null);

    public static DataRightsRestrictionTargetResolutionResult Blocked(
        string blockerCode) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionTargetResolutionStatus.Blocked,
            Targets: null,
            LimitReached: false,
            blockerCode);

    public static DataRightsRestrictionTargetResolutionResult NotFound() =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionTargetResolutionStatus.NotFound,
            Targets: null,
            LimitReached: false,
            OutcomeCode: null);

    public static DataRightsRestrictionTargetResolutionResult Stale() =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionTargetResolutionStatus.Stale,
            Targets: null,
            LimitReached: false,
            OutcomeCode: null);

    public static DataRightsRestrictionTargetResolutionResult Failed(
        string failureCode) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionTargetResolutionStatus.Failed,
            Targets: null,
            LimitReached: false,
            failureCode);
}

public enum DataRightsRestrictionTargetResolutionStatus
{
    Unknown = 0,
    Completed = 1,
    NotFound = 2,
    Stale = 3,
    Blocked = 4,
    Failed = 5
}

public sealed record DataRightsRestrictionOwnerProof(
    int ReceiptContractVersion,
    Guid ReceiptId,
    Guid OwnerOperationId,
    long ResultingOwnerRevision,
    long ResultingProjectionRevision,
    bool EffectiveRestricted,
    string ReceiptSha256,
    DateTimeOffset CompletedAtUtc);

public sealed record DataRightsRestrictionContributionResult(
    int ContractVersion,
    DataRightsRestrictionContributionStatus Status,
    DataRightsRestrictionOwnerProof? OwnerProof,
    string? OutcomeCode)
{
    public static DataRightsRestrictionContributionResult Completed(
        DataRightsRestrictionOwnerProof ownerProof) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionContributionStatus.Completed,
            ownerProof,
            OutcomeCode: null);

    public static DataRightsRestrictionContributionResult Blocked(string blockerCode) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionContributionStatus.Blocked,
            OwnerProof: null,
            blockerCode);

    public static DataRightsRestrictionContributionResult Failed(string failureCode) =>
        new(
            DataRightsRestrictionContract.CurrentVersion,
            DataRightsRestrictionContributionStatus.Failed,
            OwnerProof: null,
            failureCode);
}

public enum DataRightsRestrictionContributionStatus
{
    Unknown = 0,
    Completed = 1,
    Blocked = 2,
    Failed = 3
}

public static class DataRightsRestrictionContract
{
    public const int CurrentVersion = 3;
    public const int TargetBindingVersion = 1;
    public const int MaxReleaseTargets = 20;
    public const int OwnerKeyMaxLength = 100;
    public const int CodeMaxLength = 200;
    public const int Sha256Length = 64;
}
