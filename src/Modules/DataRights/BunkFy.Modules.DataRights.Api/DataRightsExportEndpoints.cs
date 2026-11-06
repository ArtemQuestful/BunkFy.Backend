namespace BunkFy.Modules.DataRights.Api;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class DataRightsExportEndpoints
{
    public static void MapProperty(
        RouteGroupBuilder group,
        AuthenticationAssuranceRequirement? generationAssurance,
        AuthenticationAssuranceRequirement? downloadAssurance)
    {
        group.MapGet("/{caseId:guid}/export", async (
            Guid propertyId,
            Guid caseId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            DataRightsSensitiveResponseHeaders.Apply(context.Response);
            return (await dispatcher.QueryAsync(
                new GetDataRightsExportArtifactQuery(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId),
                cancellationToken).ConfigureAwait(false))
                .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
        })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Export,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder requestExport = group.MapPost(
            "/{caseId:guid}/export",
            async (
                Guid propertyId,
                Guid caseId,
                RequestDataRightsExportRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                return actor is null
                    ? Results.Unauthorized()
                    : (await dispatcher.SendAsync(
                        new RequestDataRightsExportCommand(
                            DataRightsCaseScope.ForProperty(propertyId),
                            caseId,
                            request.IdempotencyKey,
                            request.ExpectedVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false))
                        .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
            })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Export,
                DataRightsPropertyAccessScopeResolver.ResolverName);
        requestExport.RequireAssuranceWhenConfigured(generationAssurance);

        RouteHandlerBuilder retryExport = group.MapPost(
            "/{caseId:guid}/export/{artifactId:guid}/retry",
            async (
                Guid propertyId,
                Guid caseId,
                Guid artifactId,
                RetryDataRightsExportRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                return actor is null
                    ? Results.Unauthorized()
                    : (await dispatcher.SendAsync(
                        new RetryDataRightsExportCommand(
                            DataRightsCaseScope.ForProperty(propertyId),
                            caseId,
                            artifactId,
                            request.ExpectedCaseVersion,
                            request.ExpectedArtifactVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false))
                        .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
            })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Export,
                DataRightsPropertyAccessScopeResolver.ResolverName);
        retryExport.RequireAssuranceWhenConfigured(generationAssurance);

        RouteHandlerBuilder download = group.MapGet(
            "/{caseId:guid}/export/{artifactId:guid}/download",
            async (
                Guid propertyId,
                Guid caseId,
                Guid artifactId,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
                await DownloadAsync(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    artifactId,
                    context,
                    subjectResolver,
                    dispatcher,
                    cancellationToken).ConfigureAwait(false));
        download.AddEndpointFilter<DataRightsDownloadAuditFilter>();
        download
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.DownloadExport,
                DataRightsPropertyAccessScopeResolver.ResolverName);
        download.RequireAssuranceWhenConfigured(downloadAssurance);
    }

    public static void MapTenant(
        RouteGroupBuilder group,
        AuthenticationAssuranceRequirement? generationAssurance,
        AuthenticationAssuranceRequirement? downloadAssurance)
    {
        group.MapGet("/{caseId:guid}/export", async (
            Guid caseId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            DataRightsSensitiveResponseHeaders.Apply(context.Response);
            return (await dispatcher.QueryAsync(
                new GetDataRightsExportArtifactQuery(
                    DataRightsCaseScope.Staff,
                    caseId),
                cancellationToken).ConfigureAwait(false))
                .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
        })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireTenantPermission(DataRightsAdminPermissionCodes.Export);

        RouteHandlerBuilder requestExport = group.MapPost(
            "/{caseId:guid}/export",
            async (
                Guid caseId,
                RequestDataRightsExportRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                return actor is null
                    ? Results.Unauthorized()
                    : (await dispatcher.SendAsync(
                        new RequestDataRightsExportCommand(
                            DataRightsCaseScope.Staff,
                            caseId,
                            request.IdempotencyKey,
                            request.ExpectedVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false))
                        .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
            })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireTenantPermission(DataRightsAdminPermissionCodes.Export);
        requestExport.RequireAssuranceWhenConfigured(generationAssurance);

        RouteHandlerBuilder retryExport = group.MapPost(
            "/{caseId:guid}/export/{artifactId:guid}/retry",
            async (
                Guid caseId,
                Guid artifactId,
                RetryDataRightsExportRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                return actor is null
                    ? Results.Unauthorized()
                    : (await dispatcher.SendAsync(
                        new RetryDataRightsExportCommand(
                            DataRightsCaseScope.Staff,
                            caseId,
                            artifactId,
                            request.ExpectedCaseVersion,
                            request.ExpectedArtifactVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false))
                        .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
            })
            .Produces<DataRightsExportArtifactDto>()
            .RequireTenant()
            .RequireTenantPermission(DataRightsAdminPermissionCodes.Export);
        retryExport.RequireAssuranceWhenConfigured(generationAssurance);

        RouteHandlerBuilder download = group.MapGet(
            "/{caseId:guid}/export/{artifactId:guid}/download",
            async (
                Guid caseId,
                Guid artifactId,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
                await DownloadAsync(
                    DataRightsCaseScope.Staff,
                    caseId,
                    artifactId,
                    context,
                    subjectResolver,
                    dispatcher,
                    cancellationToken).ConfigureAwait(false));
        download.AddEndpointFilter<DataRightsDownloadAuditFilter>();
        download
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .RequireTenant()
            .RequireTenantPermission(
                DataRightsAdminPermissionCodes.DownloadExport);
        download.RequireAssuranceWhenConfigured(downloadAssurance);
    }

    public sealed record RequestDataRightsExportRequest(
        Guid IdempotencyKey,
        long ExpectedVersion);

    public sealed record RetryDataRightsExportRequest(
        long ExpectedCaseVersion,
        long ExpectedArtifactVersion);

    private static async Task<IResult> DownloadAsync(
        DataRightsCaseScope scope,
        Guid caseId,
        Guid artifactId,
        HttpContext context,
        IAccessHttpSubjectResolver subjectResolver,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        DataRightsSensitiveResponseHeaders.Apply(context.Response);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        string? actor = DataRightsEndpointSupport.ResolveActor(
            context,
            subjectResolver);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        DataRightsDownloadAuditFilter.MarkHandled(context);
        Gma.Framework.Results.Result<DataRightsExportDownload> result =
            await dispatcher.SendAsync(
                new PrepareDataRightsExportDownloadCommand(
                    scope,
                    caseId,
                    artifactId,
                    actor),
                cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? result.ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes)
            : Results.Stream(
                result.Value.Content,
                contentType: "application/json",
                fileDownloadName: result.Value.FileName,
                enableRangeProcessing: false);
    }

    private static RouteHandlerBuilder RequireAssuranceWhenConfigured(
        this RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement) =>
        requirement is null
            ? endpoint
            : endpoint.RequireAuthenticationAssurance(requirement);
}
