namespace BunkFy.Modules.Workspaces.Api;

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
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Gma.Modules.AccessControl.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class WorkspaceStaffOnboardingEndpoints
{
    public static void Map(
        IEndpointRouteBuilder endpoints,
        string moduleName,
        WorkspacesApiSecurityOptions security)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/workspace-staff-enrollment")
            .WithModuleName(moduleName)
            .WithTags("Workspace Staff Enrollment")
            .RequireAuthorization();

        group.MapPost("/applications", async (
            SubmitWorkspaceStaffOnboardingRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffOnboardingSubmitter submitter,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            Result<WorkspaceStaffOnboardingSubmissionOutcome> outcome =
                await submitter.SubmitWithAuthorityOutcomeAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    request.SourceKind,
                    request.Token,
                    subject.Id,
                    request.DisplayName,
                    request.LegalName,
                    request.WorkEmail,
                    request.WorkPhone,
                    request.EmployeeNumber,
                    request.JobTitle,
                    request.Department),
                token).ConfigureAwait(false);
            return MapSubmissionOutcome(outcome).ToHttpResult(
                WorkspacesApiEndpointSupport.ErrorStatusCodes);
        }).Produces<WorkspaceStaffOnboardingDto>(StatusCodes.Status200OK);

        group.MapPost("/sources/invitations", async (
            IssueWorkspaceInvitationRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceIssuer issuer,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await issuer.IssueInvitationAsync(
                new WorkspaceInvitationIssuanceRequest(
                    request.SourceId,
                    request.RecipientEmail,
                    request.LifetimeHours,
                    request.ProfileKey,
                    request.PropertyIds ?? [],
                    subject.Id),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceIssuanceDto>(StatusCodes.Status200OK);

        group.MapPost("/sources/enrollment-links", async (
            IssueWorkspaceEnrollmentLinkRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceIssuer issuer,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await issuer.IssueEnrollmentLinkAsync(
                new WorkspaceEnrollmentLinkIssuanceRequest(
                    request.SourceId,
                    request.LifetimeHours,
                    request.MaximumClaims,
                    request.ApprovalMode,
                    request.ProfileKey,
                    request.PropertyIds ?? [],
                    subject.Id),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceIssuanceDto>(StatusCodes.Status200OK);

        group.MapGet("/sources", async (
            WorkspaceStaffOnboardingSourceKind sourceKind,
            int? page,
            int? pageSize,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceManager manager,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await manager.ListAsync(
                sourceKind,
                page ?? PageRequest.DefaultPage,
                pageSize ?? PageRequest.DefaultPageSize,
                subject.Id,
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .Produces<WorkspaceStaffJoinSourceListResponse>(StatusCodes.Status200OK);

        group.MapPost("/sources/invitations/{sourceId:guid}/revoke", async (
            Guid sourceId,
            ManageWorkspaceJoinSourceRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceManager manager,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await manager.RevokeInvitationAsync(
                sourceId,
                request.ExpectedVersion,
                subject.Id,
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceDto>(StatusCodes.Status200OK);

        group.MapPost("/sources/enrollment-links/{sourceId:guid}/disable", async (
            Guid sourceId,
            ManageWorkspaceJoinSourceRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceManager manager,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await manager.DisableEnrollmentLinkAsync(
                sourceId,
                request.ExpectedVersion,
                subject.Id,
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceDto>(StatusCodes.Status200OK);

        group.MapPost("/sources/invitations/{sourceId:guid}/replace", async (
            Guid sourceId,
            ReplaceWorkspaceJoinSourceRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceReplacementManager manager,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await manager.ReplaceInvitationAsync(
                sourceId,
                request.ReplacementSourceId,
                request.ExpectedVersion,
                request.LifetimeHours,
                subject.Id,
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceReplacementDto>(StatusCodes.Status200OK);

        group.MapPost("/sources/enrollment-links/{sourceId:guid}/replace", async (
            Guid sourceId,
            ReplaceWorkspaceJoinSourceRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IWorkspaceStaffJoinSourceReplacementManager manager,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await manager.ReplaceEnrollmentLinkAsync(
                sourceId,
                request.ReplacementSourceId,
                request.ExpectedVersion,
                request.LifetimeHours,
                subject.Id,
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffJoinSourceReplacementDto>(StatusCodes.Status200OK);

        group.MapGet("/{organizationId:guid}/applications/current", async (
            Guid organizationId,
            WorkspaceStaffOnboardingSourceKind sourceKind,
            Guid sourceId,
            HttpContext context,
            IAccessHttpSubjectResolver subjects,
            IScopeContextAccessor scopeContext,
            IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            AccessSubject? subject = WorkspacesApiEndpointSupport.ResolveUser(context, subjects);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            scopeContext.SetScope(organizationId.ToString("D"));
            return (await dispatcher.QueryAsync(
                new GetOwnWorkspaceStaffOnboardingQuery(sourceKind, sourceId, subject.Id),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        }).Produces<WorkspaceStaffOnboardingDto>(StatusCodes.Status200OK);

        group.MapGet("/applications", async (
            int? page,
            int? pageSize,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            return (await dispatcher.QueryAsync(
                new ListActionableWorkspaceStaffOnboardingQuery(
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .Produces<WorkspaceStaffOnboardingListResponse>(StatusCodes.Status200OK);

        group.MapGet("/applications/summary", async (
            int? page,
            int? pageSize,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            return (await dispatcher.QueryAsync(
                new ListActionableWorkspaceStaffOnboardingSummariesQuery(
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .Produces<WorkspaceStaffOnboardingSummaryListResponse>(StatusCodes.Status200OK);

        group.MapGet("/applications/{applicationId:guid}/review", async (
            Guid applicationId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            return (await dispatcher.QueryAsync(
                new GetWorkspaceStaffOnboardingReviewQuery(applicationId),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .Produces<WorkspaceStaffOnboardingDto>(StatusCodes.Status200OK);

        group.MapPost("/applications/{applicationId:guid}/retry", async (
            Guid applicationId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            WorkspacesApiEndpointSupport.SetNoStore(context);
            return (await dispatcher.SendAsync(
                new RetryWorkspaceStaffOnboardingCommand(applicationId),
                token).ConfigureAwait(false)).ToHttpResult(
                    WorkspacesApiEndpointSupport.ErrorStatusCodes);
        })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffOnboardingDto>(StatusCodes.Status200OK);

        group.MapPost(
            "/applications/{applicationId:guid}/recovery-attempts",
            async (
                Guid applicationId,
                WorkspaceStaffOnboardingRecoveryAttemptRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjects,
                IRequestDispatcher dispatcher,
                CancellationToken token) =>
            {
                WorkspacesApiEndpointSupport.SetNoStore(context);
                if (!security.StaffOnboardingRecoveryPostEnabled)
                {
                    return Results.NotFound();
                }

                AccessSubject? actor = WorkspacesApiEndpointSupport.ResolveUser(
                    context,
                    subjects);
                if (actor is null)
                {
                    return Results.Unauthorized();
                }

                Result<WorkspaceStaffOnboardingRecoveryAttemptDto> result =
                    await dispatcher.SendAsync(
                        new AttemptWorkspaceStaffOnboardingRecoveryCommand(
                            applicationId,
                            request.OperationId,
                            request.ExpectedApplicationVersion,
                            actor.Id),
                        token).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.ToHttpResult(
                        WorkspacesApiEndpointSupport.ErrorStatusCodes);
                }

                return result.Value.Outcome switch
                {
                    WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered =>
                        Results.Ok(result.Value),
                    WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired =>
                        Results.Json(
                            result.Value,
                            statusCode: StatusCodes.Status409Conflict),
                    WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater =>
                        RetryLater(context, result.Value),
                    _ => Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError)
                };
            })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .RequireAssuranceWhenConfigured(
                security.StaffOnboardingManagementAssurance)
            .Produces<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                StatusCodes.Status200OK)
            .Produces<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                StatusCodes.Status409Conflict)
            .Produces<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet(
            "/applications/{applicationId:guid}/recovery-attempts/latest",
            async (
                Guid applicationId,
                HttpContext context,
                IRequestDispatcher dispatcher,
                CancellationToken token) =>
            {
                WorkspacesApiEndpointSupport.SetNoStore(context);
                return (await dispatcher.QueryAsync(
                    new GetLatestWorkspaceStaffOnboardingRecoveryAttemptQuery(
                        applicationId),
                    token).ConfigureAwait(false)).ToHttpResult(
                        WorkspacesApiEndpointSupport.ErrorStatusCodes);
            })
            .RequireTenant()
            .RequireTenantPermission(
                WorkspacesPermissionCodes.StaffOnboardingManage)
            .RequireTenantPermission(AccessControlProfilePermissionCodes.Read)
            .Produces<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static IResult RetryLater(
        HttpContext context,
        WorkspaceStaffOnboardingRecoveryAttemptDto attempt)
    {
        if (attempt.RetryDirective !=
                WorkspaceStaffOnboardingRecoveryRetryDirective
                    .NewOperationAfterDelay ||
            attempt.RetryAfterSeconds is not > 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError);
        }

        context.Response.Headers.RetryAfter = attempt.RetryAfterSeconds.Value
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(
            attempt,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    internal static Result<WorkspaceStaffOnboardingDto> MapSubmissionOutcome(
        Result<WorkspaceStaffOnboardingSubmissionOutcome> outcome)
    {
        if (outcome.IsFailure)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(outcome.Error);
        }

        return outcome.Value.Kind switch
        {
            WorkspaceStaffOnboardingSubmissionOutcomeKind.Applied
                when outcome.Value.Application is not null =>
                Result.Success(outcome.Value.Application),
            WorkspaceStaffOnboardingSubmissionOutcomeKind
                .AuthorityMovedToStaff
                when outcome.Value.Application is null =>
                Result.Failure<WorkspaceStaffOnboardingDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ProfileMutationAuthorityUnavailable),
            _ => Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict)
        };
    }

    private static RouteHandlerBuilder RequireAssuranceWhenConfigured(
        this RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement) =>
        requirement is null
            ? endpoint
            : endpoint.RequireAuthenticationAssurance(requirement);
}
