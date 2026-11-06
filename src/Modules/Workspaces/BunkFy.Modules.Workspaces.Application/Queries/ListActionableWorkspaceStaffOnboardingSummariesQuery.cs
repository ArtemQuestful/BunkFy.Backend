namespace BunkFy.Modules.Workspaces.Application.Queries;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListActionableWorkspaceStaffOnboardingSummariesQuery(
    int Page,
    int PageSize) : IQuery<WorkspaceStaffOnboardingSummaryListResponse>;
