namespace BunkFy.Modules.Retention.AdminApi;

using BunkFy.Modules.Retention.Admin.Contracts;
using BunkFy.Modules.Retention.Application;
using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Queries;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Persistence;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

public sealed class RetentionAdminApiModule : IAdminApiModule
{
    public string Name => RetentionModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(
            RetentionProfiles.Default,
            "BunkFy.Modules.Retention.AdminApi");
        builder.Services.AddRetentionApplication();
        builder.AddRetentionPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints
            .MapGroup("/api/admin/" + RetentionModuleMetadata.Name)
            .WithModuleName(this.Name)
            .WithTags("Retention Admin")
            .RequireAuthorization();
        group.AddEndpointFilter(SensitiveResponseFilter);

        group.MapGet("/schedules", async (
            int? page,
            int? pageSize,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    RetentionAdminOperationNames.ScheduleList,
                    RetentionAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListRetentionScheduleHealthQuery(
                        page ?? PageRequest.DefaultPage,
                        pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RetentionScheduleHealthListResponse>(StatusCodes.Status200OK);

        group.MapPost("/runs/{runId:guid}/retry", async (
            Guid runId,
            RetryRetentionRunRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            ITenantContext tenantContext,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    RetentionAdminOperationNames.RunRetry,
                    RetentionAdminPermissions.Retry),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RequestRetentionRunRetryCommand(
                            runId,
                            tenantContext.TenantId ?? string.Empty,
                            request.ScheduledAtUtc),
                        token)
                    : Task.FromResult(
                        Result.Failure<RetentionRunRetryReceiptDto>(
                            AdminErrors.ConfirmationRequired)),
                cancellationToken,
                onSuccess: receipt => Results.Accepted(value: receipt),
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RetentionRunRetryReceiptDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
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

    private static readonly ApiErrorStatusCodeMap AdminErrorStatusCodes =
        ApiErrorStatusCodeMap.Create(
            new(
                RetentionApplicationErrors.TaskRunUnavailable.Code,
                StatusCodes.Status404NotFound),
            new(
                RetentionApplicationErrors.ScheduleRetryEvidenceChanged.Code,
                StatusCodes.Status409Conflict),
            new(
                "Retention.RecoveryTransitionInvalid",
                StatusCodes.Status409Conflict),
            new(
                RetentionApplicationErrors.WorkspaceProcessingRestricted.Code,
                StatusCodes.Status423Locked),
            new(
                RetentionApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code,
                StatusCodes.Status503ServiceUnavailable));

    public sealed record RetryRetentionRunRequest(
        bool Confirmed,
        DateTimeOffset? ScheduledAtUtc);
}
