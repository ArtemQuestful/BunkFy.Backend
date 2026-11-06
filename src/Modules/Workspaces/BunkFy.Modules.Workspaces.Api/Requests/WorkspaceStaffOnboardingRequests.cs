namespace BunkFy.Modules.Workspaces.Api.Requests;

using BunkFy.Modules.Workspaces.Contracts;

public sealed record SubmitWorkspaceStaffOnboardingRequest(
    WorkspaceStaffOnboardingSourceKind SourceKind,
    string Token,
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department);

public sealed record WorkspaceStaffOnboardingRecoveryAttemptRequest(
    Guid OperationId,
    long ExpectedApplicationVersion);
