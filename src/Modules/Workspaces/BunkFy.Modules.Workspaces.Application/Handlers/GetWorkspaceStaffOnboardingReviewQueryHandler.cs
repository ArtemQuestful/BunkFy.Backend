namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class GetWorkspaceStaffOnboardingReviewQueryHandler(
    WorkspaceStaffOnboardingManagerReviewReader reviews)
    : IQueryHandler<GetWorkspaceStaffOnboardingReviewQuery, WorkspaceStaffOnboardingDto>
{
    public Task<Result<WorkspaceStaffOnboardingDto>> HandleAsync(
        GetWorkspaceStaffOnboardingReviewQuery query,
        CancellationToken cancellationToken) => reviews.ReadReviewAsync(
            query.ApplicationId, cancellationToken);
}
