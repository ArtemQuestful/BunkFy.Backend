namespace BunkFy.Modules.Workspaces.Application.Ports;

public interface IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
{
    Task<bool> CanMutateAsync(
        string tenantId,
        string? subjectId,
        CancellationToken cancellationToken);
}
