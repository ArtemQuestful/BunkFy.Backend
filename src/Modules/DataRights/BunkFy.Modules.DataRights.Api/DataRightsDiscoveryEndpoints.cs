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
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class DataRightsDiscoveryEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{caseId:guid}/subjects", (
            Guid propertyId,
            Guid caseId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => GetSelectedSubjectsAsync(
                DataRightsCaseScope.ForProperty(propertyId),
                caseId,
                context,
                dispatcher,
                cancellationToken))
            .Produces<DataRightsSelectedSubjectsResponse>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Discover,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/{caseId:guid}/review-evidence", (
            Guid propertyId,
            Guid caseId,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => GetSelectedSubjectsAsync(
                DataRightsCaseScope.ForProperty(propertyId),
                caseId,
                context,
                dispatcher,
                cancellationToken))
            .Produces<DataRightsSelectedSubjectsResponse>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Review,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/subjects/discover", async (
            Guid propertyId,
            Guid caseId,
            DiscoverDataRightsSubjectsRequest request,
            HttpContext context,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            DataRightsSensitiveResponseHeaders.Apply(context.Response);
            Result<DataRightsSubjectDiscoveryResponse> result =
                await dispatcher.QueryAsync(
                new DiscoverDataRightsSubjectsQuery(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    new DataRightsSubjectLookup(
                        request.RecordId,
                        request.Email,
                        request.Phone,
                        request.Name,
                        request.DateOfBirth,
                        request.AccountSubjectId),
                    request.OwnerKey),
                    cancellationToken).ConfigureAwait(false);
            return DataRightsEndpointSupport.ToHttpResult(context, result);
        })
            .Produces<DataRightsSubjectDiscoveryResponse>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Discover,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/subjects/select", async (
            Guid propertyId,
            Guid caseId,
            SelectDataRightsSubjectRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            DataRightsSensitiveResponseHeaders.Apply(context.Response);
            return await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new SelectDataRightsSubjectCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.Coordinate,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false);
        })
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Discover,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/subjects/unselect", async (
            Guid propertyId,
            Guid caseId,
            UnselectDataRightsSubjectRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            DataRightsSensitiveResponseHeaders.Apply(context.Response);
            return await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new UnselectDataRightsSubjectCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.Coordinate,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false);
        })
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Discover,
                DataRightsPropertyAccessScopeResolver.ResolverName);
    }

    public sealed record DiscoverDataRightsSubjectsRequest(
        Guid? RecordId,
        string? Email,
        string? Phone,
        string? Name,
        DateOnly? DateOfBirth,
        string? AccountSubjectId,
        string? OwnerKey);

    public sealed record SelectDataRightsSubjectRequest(
        DataRightsSubjectCoordinate Coordinate,
        long ExpectedVersion);

    public sealed record UnselectDataRightsSubjectRequest(
        DataRightsSubjectCoordinateKey Coordinate,
        long ExpectedVersion);

    internal static async Task<IResult> GetSelectedSubjectsAsync(
        DataRightsCaseScope scope,
        Guid caseId,
        HttpContext context,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        DataRightsSensitiveResponseHeaders.Apply(context.Response);
        return (await dispatcher.QueryAsync(
            new GetDataRightsSelectedSubjectsQuery(scope, caseId),
            cancellationToken).ConfigureAwait(false))
            .ToHttpResult(DataRightsEndpointSupport.ErrorStatusCodes);
    }
}
