namespace BunkFy.Modules.Workspaces.Application.Ports;

public interface IWorkspaceStaffOnboardingRestorationSuppressionReader
{
    Task<WorkspaceStaffOnboardingRestorationSuppressionState> ReadAsync(
        string scopeId,
        Guid staffMemberId,
        string authSubjectId,
        CancellationToken cancellationToken);
}

public enum WorkspaceStaffOnboardingRestorationSuppressionState
{
    None = 1,
    Suppressed = 2,
    Conflict = 3
}
