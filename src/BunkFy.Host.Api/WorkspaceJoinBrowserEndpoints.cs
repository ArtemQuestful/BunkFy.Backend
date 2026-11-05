namespace BunkFy.Host.Api;

using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Modules.Organizations.Api;
using Gma.Modules.Organizations.Application.Commands;
using Gma.Modules.Organizations.Application.Queries;
using Gma.Framework.Cqrs;

internal static class WorkspaceJoinBrowserEndpoints
{
    internal static IEndpointRouteBuilder MapWorkspaceJoinBrowserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup(
            WorkspaceJoinBrowserContinuation.RoutePrefix + "/{contextId:guid}")
            .WithTags("Workspace Join")
            .AddEndpointFilter(GuardAsync);

        group.MapPost("", PrepareAsync).Produces<WorkspaceJoinBrowserContextDto>();
        group.MapPost("/preview", PreviewAsync);
        group.MapPost("/applications", SubmitAsync).Produces<WorkspaceStaffOnboardingDto>().RequireAuthorization();
        group.MapPost("/join", JoinAsync).RequireAuthorization();
        group.MapDelete("", Clear);
        return endpoints;
    }

    private static async ValueTask<object?> GuardAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        if (!Guid.TryParse(context.Request.RouteValues["contextId"]?.ToString(), out Guid contextId) ||
            !WorkspaceJoinBrowserContinuation.IsAdmitted(context, contextId))
        {
            return Denied();
        }

        return await next(invocation).ConfigureAwait(false);
    }

    private static async Task<IResult> PrepareAsync(
        Guid contextId,
        PrepareWorkspaceJoinBrowserRequest request,
        HttpContext context,
        WorkspaceJoinBrowserContinuation continuation,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (request.SourceKind is not (WorkspaceStaffOnboardingSourceKind.Invitation or WorkspaceStaffOnboardingSourceKind.EnrollmentLink) ||
            request.Token is not { Length: > 0 and <= 256 })
        {
            return Results.Problem(title: "Workspaces.JoinInputInvalid",
                detail: "The complete invitation or enrollment link is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate through the original public preview operation, including its
        // error contract. Preview still permits inspecting an expired source
        // so an already-pending application can continue to recover.
        if (request.SourceKind == WorkspaceStaffOnboardingSourceKind.Invitation)
        {
            var result = await dispatcher.QueryAsync(new PreviewOrganizationInvitationQuery(request.Token), cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return result.ToOrganizationsHttpResult();
            }
        }
        else
        {
            var result = await dispatcher.QueryAsync(new PreviewOrganizationEnrollmentLinkQuery(request.Token), cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return result.ToOrganizationsHttpResult();
            }
        }

        return continuation.TryStore(context, contextId, request.SourceKind, request.Token)
            ? Results.Ok(new WorkspaceJoinBrowserContextDto(contextId, request.SourceKind))
            : Denied();
    }

    private static async Task<IResult> PreviewAsync(
        Guid contextId,
        HttpContext context,
        WorkspaceJoinBrowserContinuation continuation,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!continuation.TryRead(context, contextId, out var kind, out string token))
        {
            return Unavailable();
        }

        return kind == WorkspaceStaffOnboardingSourceKind.Invitation
            ? (await dispatcher.QueryAsync(new PreviewOrganizationInvitationQuery(token), cancellationToken).ConfigureAwait(false))
                .ToOrganizationsHttpResult()
            : (await dispatcher.QueryAsync(new PreviewOrganizationEnrollmentLinkQuery(token), cancellationToken).ConfigureAwait(false))
                .ToOrganizationsHttpResult();
    }

    private static async Task<IResult> SubmitAsync(
        Guid contextId,
        WorkspaceJoinStaffProfileRequest request,
        HttpContext context,
        WorkspaceJoinBrowserContinuation continuation,
        IAccessHttpSubjectResolver subjects,
        IWorkspaceStaffOnboardingSubmitter submitter,
        CancellationToken cancellationToken)
    {
        AccessSubject? subject = subjects.ResolveSubject(context);
        if (subject?.Kind != AccessSubjectKind.User)
        {
            return Results.Unauthorized();
        }

        if (!continuation.TryRead(context, contextId, out var kind, out string token))
        {
            return Unavailable();
        }

        // Retain the authoritative-scope submitter, verified-identity checks,
        // frozen proposal and existing Workspaces journal/HTTP result contract.
        return (await submitter.SubmitAsync(new SubmitWorkspaceStaffOnboardingCommand(
            kind, token, subject.Id, request.DisplayName, request.LegalName,
            request.WorkEmail, request.WorkPhone, request.EmployeeNumber,
            request.JobTitle, request.Department), cancellationToken).ConfigureAwait(false))
            .ToWorkspacesHttpResult();
    }

    private static async Task<IResult> JoinAsync(
        Guid contextId,
        HttpContext context,
        WorkspaceJoinBrowserContinuation continuation,
        IAccessHttpSubjectResolver subjects,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        AccessSubject? subject = subjects.ResolveSubject(context);
        if (subject?.Kind != AccessSubjectKind.User)
        {
            return Results.Unauthorized();
        }

        if (!continuation.TryRead(context, contextId, out var kind, out string token))
        {
            return Unavailable();
        }

        // Dispatch the unchanged owner commands: this facade grants no access
        // and caches no validity, expiry, capacity, claim or membership decision.
        return kind == WorkspaceStaffOnboardingSourceKind.Invitation
            ? (await dispatcher.SendAsync(new AcceptOrganizationInvitationCommand(token, subject.Id, "user:" + subject.Id), cancellationToken)
                .ConfigureAwait(false)).ToOrganizationsHttpResult()
            : (await dispatcher.SendAsync(new ClaimOrganizationEnrollmentLinkCommand(token, subject.Id, "user:" + subject.Id), cancellationToken)
                .ConfigureAwait(false)).ToOrganizationsHttpResult();
    }

    private static IResult Clear(Guid contextId, HttpContext context, WorkspaceJoinBrowserContinuation continuation) =>
        continuation.TryClear(context, contextId) ? Results.NoContent() : Denied();

    private static IResult Unavailable() => Results.Problem(
        title: "Workspaces.JoinContextUnavailable",
        detail: "Reopen the complete invitation or enrollment link to continue.",
        statusCode: StatusCodes.Status404NotFound);

    private static IResult Denied() => Results.Problem(
        title: "Workspaces.JoinContextDenied",
        detail: "The browser join request could not be verified.",
        statusCode: StatusCodes.Status403Forbidden);
}

public sealed record PrepareWorkspaceJoinBrowserRequest(WorkspaceStaffOnboardingSourceKind SourceKind, string Token);

public sealed record WorkspaceJoinBrowserContextDto(Guid ContextId, WorkspaceStaffOnboardingSourceKind SourceKind);

public sealed record WorkspaceJoinStaffProfileRequest(
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department);
