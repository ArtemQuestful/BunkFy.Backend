namespace BunkFy.Modules.Workspaces.Api;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Api.Requests;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class WorkspaceStaffOnboardingDataRightsEndpoints
{
    public static void Map(
        IEndpointRouteBuilder endpoints,
        string moduleName)
    {
        RouteGroupBuilder group = endpoints
            .MapGroup(
                "/api/workspace-staff-enrollment/data-rights-corrections")
            .WithModuleName(moduleName)
            .WithTags("Workspace Staff Enrollment")
            .RequireAuthorization();

        group.MapGet("/{applicationId:guid}", async (
            Guid applicationId,
            Guid executionId,
            Guid caseId,
            long approvalRevision,
            long expectedVersion,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? actor =
                WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            return (await dispatcher.QueryAsync(
                new
                    GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQuery(
                        executionId,
                        caseId,
                        approvalRevision,
                        applicationId,
                        expectedVersion,
                        FormatActor(actor)),
                cancellationToken).ConfigureAwait(false))
                .ToHttpResult(WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .Produces<
                WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>(
                StatusCodes.Status200OK)
            .RequireTenant()
            .RequireTenantPermission(
                DataRightsAdminPermissionCodes.Execute);

        group.MapPost("", async (
            WorkspaceStaffOnboardingDataRightsCorrectionRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? actor =
                WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>
                outcome = await dispatcher.SendAsync(
                new ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand(
                    request.ExecutionId,
                    request.CaseId,
                    request.ApprovalRevision,
                    request.ApplicationId,
                    request.ExpectedVersion,
                    request.DisplayName,
                    request.LegalName,
                    request.WorkEmail,
                    request.WorkPhone,
                    request.EmployeeNumber,
                    request.JobTitle,
                    request.Department,
                    FormatActor(actor)),
                cancellationToken).ConfigureAwait(false);
            return MapCorrectionOutcome(outcome).ToHttpResult(
                WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .Produces<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                StatusCodes.Status200OK)
            .RequireTenant()
            .RequireTenantPermission(
                DataRightsAdminPermissionCodes.Execute);
    }

    private static string FormatActor(AccessSubject actor) =>
        $"{AccessSubjectKindNames.GetName(actor.Kind)}:{actor.Id}";

    internal static Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>
        MapCorrectionOutcome(
            Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>
                outcome)
    {
        if (outcome.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                outcome.Error);
        }

        return outcome.Value.Kind switch
        {
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind.Applied
                when outcome.Value.Receipt is not null =>
                Result.Success(outcome.Value.Receipt),
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind
                .AuthorityMovedToStaff
                when outcome.Value.Receipt is null =>
                Result.Failure<
                    WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .CorrectionTargetUnavailable),
            _ => Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict)
        };
    }
}
