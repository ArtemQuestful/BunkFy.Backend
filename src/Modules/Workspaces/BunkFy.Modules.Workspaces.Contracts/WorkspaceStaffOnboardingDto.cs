namespace BunkFy.Modules.Workspaces.Contracts;

public sealed record WorkspaceStaffOnboardingDto(
    Guid ApplicationId,
    Guid OrganizationId,
    WorkspaceStaffOnboardingSourceKind SourceKind,
    Guid SourceId,
    Guid? ClaimId,
    long? ClaimVersion,
    string SubjectId,
    string? VerifiedAccountEmail,
    string? DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    WorkspaceStaffOnboardingStatus Status,
    Guid? StaffMemberId,
    long Version,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

public sealed record WorkspaceStaffOnboardingListResponse(
    IReadOnlyList<WorkspaceStaffOnboardingDto> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record WorkspaceStaffOnboardingSummaryListResponse(
    IReadOnlyList<WorkspaceStaffOnboardingActionableSummaryDto> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record WorkspaceStaffOnboardingActionableSummaryDto(
    Guid ApplicationId,
    WorkspaceStaffOnboardingSourceKind SourceKind,
    Guid SourceId,
    Guid? ClaimId,
    long? ClaimVersion,
    WorkspaceStaffOnboardingStatus Status,
    long Version,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    bool HasStaffTarget);
