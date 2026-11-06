namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;

public interface IWorkspaceStaffOnboardingRepository
{
    Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
        Guid applicationId,
        CancellationToken cancellationToken);
    Task<WorkspaceStaffOnboarding?> GetAsync(Guid applicationId, CancellationToken cancellationToken);
    Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
        Guid applicationId,
        CancellationToken cancellationToken);
    Task<WorkspaceStaffOnboarding?> GetOperationalBySourceAndSubjectAsync(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        string subjectId,
        CancellationToken cancellationToken);
    Task<Guid?> FindIdBySourceAndSubjectAsync(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        string subjectId,
        CancellationToken cancellationToken);
    Task<WorkspaceStaffOnboarding?> GetByClaimAsync(Guid claimId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceStaffOnboarding>> ListActiveBySourceAsync(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken);
    Task<WorkspaceStaffOnboardingSummaryListResponse> ListActionableAsync(
        PageRequest page,
        CancellationToken cancellationToken);
    Task ReloadAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken);
    Task AddAsync(WorkspaceStaffOnboarding application, CancellationToken cancellationToken);
}

public sealed record WorkspaceStaffOnboardingCoordinate(
    Guid ApplicationId,
    WorkspaceStaffOnboardingSource SourceKind,
    Guid SourceId);
