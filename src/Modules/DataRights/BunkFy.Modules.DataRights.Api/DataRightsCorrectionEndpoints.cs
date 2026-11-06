namespace BunkFy.Modules.DataRights.Api;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class DataRightsCorrectionEndpoints
{
    public static void MapProperty(
        RouteGroupBuilder group,
        AuthenticationAssuranceRequirement? executionAssurance)
    {
        group.MapGet(
            "/{caseId:guid}/correction",
            async (
                Guid propertyId,
                Guid caseId,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                if (actor is null)
                {
                    return Results.Unauthorized();
                }

                Result<DataRightsCorrectionExecutionDetailsDto> result =
                    await dispatcher.QueryAsync(
                    new GetDataRightsCorrectionExecutionQuery(
                        DataRightsCaseScope.ForProperty(propertyId),
                        caseId,
                        actor),
                    cancellationToken).ConfigureAwait(false);
                return DataRightsEndpointSupport.ToHttpResult(context, result);
            })
            .Produces<DataRightsCorrectionExecutionDetailsDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Execute,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder start = group.MapPost(
            "/{caseId:guid}/correction",
            async (
                Guid propertyId,
                Guid caseId,
                StartDataRightsCorrectionRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                if (actor is null)
                {
                    return Results.Unauthorized();
                }

                Result<DataRightsCorrectionExecutionDto> result =
                    await dispatcher.SendAsync(
                        new StartDataRightsCorrectionExecutionCommand(
                            DataRightsCaseScope.ForProperty(propertyId),
                            caseId,
                            request.ExecutionId,
                            request.ExpectedVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false);
                return DataRightsEndpointSupport.ToHttpResult(context, result);
            })
            .Produces<DataRightsCorrectionExecutionDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Execute,
                DataRightsPropertyAccessScopeResolver.ResolverName);
        if (executionAssurance is not null)
        {
            start.RequireAuthenticationAssurance(executionAssurance);
        }
    }

    public static void MapTenant(
        RouteGroupBuilder group,
        AuthenticationAssuranceRequirement? executionAssurance)
    {
        group.MapGet(
            "/{caseId:guid}/correction",
            async (
                Guid caseId,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                if (actor is null)
                {
                    return Results.Unauthorized();
                }

                Result<DataRightsCorrectionExecutionDetailsDto> result =
                    await dispatcher.QueryAsync(
                    new GetDataRightsCorrectionExecutionQuery(
                        DataRightsCaseScope.Staff,
                        caseId,
                        actor),
                    cancellationToken).ConfigureAwait(false);
                return DataRightsEndpointSupport.ToHttpResult(context, result);
            })
            .Produces<DataRightsCorrectionExecutionDetailsDto>()
            .RequireTenant()
            .RequireTenantPermission(DataRightsAdminPermissionCodes.Execute);

        RouteHandlerBuilder start = group.MapPost(
            "/{caseId:guid}/correction",
            async (
                Guid caseId,
                StartDataRightsCorrectionRequest request,
                HttpContext context,
                IAccessHttpSubjectResolver subjectResolver,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                DataRightsSensitiveResponseHeaders.Apply(context.Response);
                string? actor = DataRightsEndpointSupport.ResolveActor(
                    context,
                    subjectResolver);
                if (actor is null)
                {
                    return Results.Unauthorized();
                }

                Result<DataRightsCorrectionExecutionDto> result =
                    await dispatcher.SendAsync(
                        new StartDataRightsCorrectionExecutionCommand(
                            DataRightsCaseScope.Staff,
                            caseId,
                            request.ExecutionId,
                            request.ExpectedVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false);
                return DataRightsEndpointSupport.ToHttpResult(context, result);
            })
            .Produces<DataRightsCorrectionExecutionDto>()
            .RequireTenant()
            .RequireTenantPermission(DataRightsAdminPermissionCodes.Execute);
        if (executionAssurance is not null)
        {
            start.RequireAuthenticationAssurance(executionAssurance);
        }
    }

    public sealed record StartDataRightsCorrectionRequest(
        Guid ExecutionId,
        long ExpectedVersion);
}
