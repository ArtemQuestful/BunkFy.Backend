namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Ports;

internal sealed class
    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(
        bool allowed = true,
        List<string>? calls = null)
    : IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
{
    public int CallCount { get; private set; }
    public string? TenantId { get; private set; }
    public string? SubjectId { get; private set; }

    public Task<bool> CanMutateAsync(
        string tenantId,
        string? subjectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.CallCount++;
        this.TenantId = tenantId;
        this.SubjectId = subjectId;
        calls?.Add("identity-anchor-fence");
        return Task.FromResult(allowed);
    }
}
