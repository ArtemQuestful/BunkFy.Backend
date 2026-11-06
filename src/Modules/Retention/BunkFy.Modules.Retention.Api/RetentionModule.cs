namespace BunkFy.Modules.Retention.Api;

using BunkFy.Modules.Retention.Application;
using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Queries;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Persistence;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Modules;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class RetentionModule : IModule
{
    public string Name => RetentionModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(
            RetentionProfiles.Default,
            "BunkFy.Modules.Retention.Api");
        builder.Services.AddOptions<RetentionApiSecurityOptions>();
        builder.Services.AddRetentionApplication();
        builder.AddRetentionPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RetentionApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<RetentionApiSecurityOptions>>()
            .Value;
        RouteGroupBuilder group = endpoints.MapGroup("/api/retention")
            .WithModuleName(this.Name)
            .WithTags("Retention")
            .RequireAuthorization();
        group.AddEndpointFilter(SensitiveResponseFilter);

        group.MapGet("/schedules", async (
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListRetentionScheduleHealthQuery(
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .Produces<RetentionScheduleHealthListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireTenantPermission(RetentionPermissionCodes.Read);

        RouteHandlerBuilder retry = group.MapPost(
            "/runs/{runId:guid}/retry",
            async (
                Guid runId,
                RetryRetentionScheduleRequest request,
                ITenantContext tenantContext,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                Result<RetentionRunRetryReceiptDto> result = request.Confirmed
                    ? await dispatcher.SendAsync(
                        new RequestRetentionRunRetryCommand(
                            runId,
                            tenantContext.TenantId ?? string.Empty,
                            ScheduledAtUtc: null,
                            new RetentionRunRetryExpectation(
                                request.OwnerKey,
                                request.DataClassKey,
                                request.TargetScopeKind,
                                request.PropertyId,
                                request.ExecutionPolicyVersion,
                                request.EvidenceVersion)),
                        cancellationToken).ConfigureAwait(false)
                    : Result.Failure<RetentionRunRetryReceiptDto>(
                        RetentionApplicationErrors.RetryConfirmationRequired);
                return result.IsFailure
                    ? result.ToHttpResult(RetryErrorStatusCodes)
                    : Results.Accepted(value: result.Value);
            })
            .Produces<RetentionRunRetryReceiptDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireTenantPermission(RetentionPermissionCodes.Retry);
        RequireAssuranceWhenConfigured(retry, security.RetryAssurance);
    }

    private static async ValueTask<object?> SensitiveResponseFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        MarkSensitiveResponse(context.HttpContext);
        return await next(context).ConfigureAwait(false);
    }

    private static void MarkSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static readonly ApiErrorStatusCodeMap RetryErrorStatusCodes =
        ApiErrorStatusCodeMap.Create(
            new(
                RetentionApplicationErrors.TaskRunUnavailable.Code,
                StatusCodes.Status404NotFound),
            new(
                RetentionApplicationErrors.ScheduleRetryEvidenceChanged.Code,
                StatusCodes.Status409Conflict),
            new(
                RetentionApplicationErrors.RetryConfirmationRequired.Code,
                StatusCodes.Status400BadRequest),
            new(
                "Retention.RecoveryTransitionInvalid",
                StatusCodes.Status409Conflict),
            new(
                RetentionApplicationErrors.WorkspaceProcessingRestricted.Code,
                StatusCodes.Status423Locked),
            new(
                RetentionApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code,
                StatusCodes.Status503ServiceUnavailable));

    private static void RequireAssuranceWhenConfigured(
        RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement)
    {
        if (requirement is not null)
        {
            endpoint.RequireAuthenticationAssurance(requirement);
        }
    }

}
