namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Domain;

public interface IWorkspaceStaffIdentityAnchorCutoverSourceReader
{
    Task<WorkspaceStaffIdentityAnchorSourcePage> ListRelevantPageAsync(
        Guid? afterApplicationId,
        int pageSize,
        CancellationToken cancellationToken);
}

public static class WorkspaceStaffIdentityAnchorCutoverSourceLimits
{
    public const int PageSize = 200;
}

public sealed record WorkspaceStaffIdentityAnchorSourcePage(
    IReadOnlyList<WorkspaceStaffIdentityAnchorSourceRecord> Records,
    Guid? NextApplicationId,
    bool HasMore);

public sealed record WorkspaceStaffIdentityAnchorSourceRecord(
    Guid ApplicationId,
    Guid? StaffMemberId,
    string SubjectId,
    WorkspaceStaffOnboardingState Status);
