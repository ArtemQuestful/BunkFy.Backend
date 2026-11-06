namespace BunkFy.Modules.DataRights.Contracts;

public sealed record DataRightsSubjectCandidate(
    DataRightsSubjectCoordinate Coordinate,
    string DisplayName,
    string? EmailHint,
    string? PhoneHint);

public sealed record DataRightsSubjectDiscoveryRequest(
    string TenantId,
    DataRightsCaseType CaseType,
    Guid? PropertyId,
    DataRightsSubjectLookup Lookup,
    int MaxCandidates);

public enum DataRightsSubjectDiscoveryStatus
{
    Unknown = 0,
    Succeeded = 1,
    ScopeUnavailable = 2,
    RetryRequired = 3
}

public sealed record DataRightsSubjectDiscoveryResult(
    DataRightsSubjectDiscoveryStatus Status,
    IReadOnlyCollection<DataRightsSubjectCandidate> Candidates)
{
    public static DataRightsSubjectDiscoveryResult Success(
        IReadOnlyCollection<DataRightsSubjectCandidate> candidates) =>
        new(DataRightsSubjectDiscoveryStatus.Succeeded, candidates);

    public static DataRightsSubjectDiscoveryResult ScopeUnavailable() =>
        new(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, []);

    public static DataRightsSubjectDiscoveryResult RetryRequired() =>
        new(DataRightsSubjectDiscoveryStatus.RetryRequired, []);
}

public sealed record DataRightsSubjectDiscoveryResponse(
    IReadOnlyCollection<DataRightsSubjectCandidate> Candidates,
    bool LimitReached);
