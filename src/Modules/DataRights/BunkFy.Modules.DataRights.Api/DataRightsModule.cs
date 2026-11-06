namespace BunkFy.Modules.DataRights.Api;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Persistence;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Modules;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class DataRightsModule : IModule
{
    public string Name => DataRightsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(DataRightsProfiles.Default, "BunkFy.Modules.DataRights.Api");
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAccessHttpScopeResolver, DataRightsPropertyAccessScopeResolver>());
        builder.Services.AddOptions<DataRightsApiSecurityOptions>();
        builder.Services.AddDataRightsApplication();
        builder.Services.AddDataRightsUnavailableTenantTerminationTaskScheduling();
        builder.AddDataRightsPersistence();
        builder.AddDataRightsRestoreReadinessGate();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DataRightsApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<DataRightsApiSecurityOptions>>()
            .Value;
        RouteGroupBuilder group = endpoints
            .MapGroup("/api/data-rights/properties/{propertyId:guid}/cases")
            .WithModuleName(this.Name)
            .WithTags("Data rights")
            .RequireAuthorization();
        group.AddEndpointFilter(DataRightsEndpointSupport.SensitiveResponseFilter);

        group.MapGet("", async (
            Guid propertyId,
            DataRightsCaseStatus? status,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListDataRightsCasesQuery(
                    DataRightsCaseScope.ForProperty(propertyId),
                    status,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(
                    DataRightsEndpointSupport.ErrorStatusCodes))
            .Produces<DataRightsCaseListResponse>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Read,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/{caseId:guid}", async (
            Guid propertyId,
            Guid caseId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetDataRightsCaseQuery(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(
                    DataRightsEndpointSupport.ErrorStatusCodes))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Read,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("", async (
            Guid propertyId,
            CreateDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = DataRightsEndpointSupport.ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CreateDataRightsCaseCommand(
                        request.OperationId,
                        DataRightsCaseScope.ForProperty(propertyId),
                        request.RequestedOperations,
                        request.RestrictionDirective,
                        request.RequesterRelationship,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(
                        DataRightsEndpointSupport.ErrorStatusCodes);
        })
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Create,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/requester-verification", async (
            Guid propertyId,
            Guid caseId,
            RecordRequesterVerificationRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new RecordRequesterVerificationCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.Verified,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Review,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/controller-routing", async (
            Guid propertyId,
            Guid caseId,
            VersionedDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new RecordControllerRoutingCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Review,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/discovery", async (
            Guid propertyId,
            Guid caseId,
            VersionedDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new BeginDataRightsDiscoveryCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Discover,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/review", async (
            Guid propertyId,
            Guid caseId,
            VersionedDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new RequireDataRightsReviewCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Review,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/decision", async (
            Guid propertyId,
            Guid caseId,
            VersionedDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new BeginDataRightsDecisionCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Decide,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/decision/outcome", async (
            Guid propertyId,
            Guid caseId,
            RecordDataRightsDecisionRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new RecordDataRightsDecisionCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.Decision,
                    request.Reason,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Decide,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{caseId:guid}/cancel", async (
            Guid propertyId,
            Guid caseId,
            VersionedDataRightsCaseRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) => await DataRightsEndpointSupport.DispatchAsync(
                context,
                subjectResolver,
                actor => new CancelDataRightsCaseCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    caseId,
                    request.ExpectedVersion,
                    actor),
                dispatcher,
                cancellationToken).ConfigureAwait(false))
            .Produces<DataRightsCaseDto>()
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Manage,
                DataRightsPropertyAccessScopeResolver.ResolverName);

        DataRightsDiscoveryEndpoints.Map(group);
        DataRightsExecutionEndpoints.MapProperty(
            group,
            security.AnonymisationExecutionAssurance);
        DataRightsRestrictionEndpoints.MapProperty(
            group,
            security.RestrictionExecutionAssurance);
        DataRightsCorrectionEndpoints.MapProperty(
            group,
            security.CorrectionExecutionAssurance);
        DataRightsExportEndpoints.MapProperty(
            group,
            security.ExportGenerationAssurance,
            security.ExportDownloadAssurance);
        DataRightsTenantEndpoints.Map(
            endpoints,
            this.Name,
            security.AnonymisationExecutionAssurance,
            security.RestrictionExecutionAssurance,
            security.CorrectionExecutionAssurance,
            security.ExportGenerationAssurance,
            security.ExportDownloadAssurance);
    }

    public sealed record CreateDataRightsCaseRequest(
        Guid OperationId,
        DataRightsOperation RequestedOperations,
        DataRightsRestrictionDirective RestrictionDirective,
        DataRightsRequesterRelationship RequesterRelationship);

    public sealed record RecordRequesterVerificationRequest(
        bool Verified,
        long ExpectedVersion);

    public sealed record RecordDataRightsDecisionRequest(
        DataRightsDecisionOutcome Decision,
        DataRightsDecisionReason Reason,
        long ExpectedVersion);

    public sealed record VersionedDataRightsCaseRequest(long ExpectedVersion);

}
