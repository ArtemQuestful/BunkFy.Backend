namespace BunkFy.Modules.DataRights.Contracts;

public sealed record DataRightsRestrictionReleaseTargetDto(
    string OwnerKey,
    Guid OwnerOperationId,
    long OwnerOperationVersion,
    DateTimeOffset SelectedAtUtc);

public sealed record DataRightsRestrictionReleaseTargetCandidateDto(
    Guid OwnerOperationId,
    long OwnerOperationVersion,
    Guid SourceCaseId,
    DateTimeOffset AppliedAtUtc);

public sealed record DataRightsRestrictionReleaseTargetListResponse(
    long CaseVersion,
    IReadOnlyCollection<DataRightsRestrictionReleaseTargetCandidateDto> Targets,
    bool LimitReached);
