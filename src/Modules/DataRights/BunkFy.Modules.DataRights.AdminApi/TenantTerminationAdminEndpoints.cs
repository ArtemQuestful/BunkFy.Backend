namespace BunkFy.Modules.DataRights.AdminApi;

using System.Security.Claims;
using BunkFy.Modules.DataRights.Admin.Contracts;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class TenantTerminationAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string moduleName)
    {
        RouteGroupBuilder group = endpoints
            .MapGroup("/api/admin/data-rights/tenant-termination")
            .WithModuleName(moduleName)
            .WithTags("Data Rights Tenant Termination")
            .RequireAuthorization();

        group.MapGet("/{caseId:guid}", async (
            Guid caseId,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationStatus,
                    DataRightsAdminPermissions.TenantTerminationRead),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetTenantTerminationOperatorStatusQuery(caseId),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationOperatorStatusDto>();

        group.MapGet(
            "/{caseId:guid}/processes/{processId:guid}/export/{artifactId:guid}/download",
            async (
                Guid caseId,
                Guid processId,
                Guid artifactId,
                HttpContext context,
                AdminApiExecutor executor,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                ApplyDownloadResponseHeaders(context.Response);
                return await executor.ExecuteAsync(
                    context,
                    AdminOperation.Create(
                        DataRightsAdminOperationNames
                            .TenantTerminationExportDownload,
                        DataRightsAdminPermissions
                            .TenantTerminationExportDownload),
                    requireTenant: true,
                    token => dispatcher.SendAsync(
                        new PrepareTenantTerminationExportDownloadCommand(
                            caseId,
                            processId,
                            artifactId,
                            Actor(context)),
                        token),
                    cancellationToken,
                    onSuccess: download => Results.Stream(
                        download.Content,
                        contentType: "application/zip",
                        fileDownloadName: download.FileName,
                        enableRangeProcessing: false),
                    errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");

        group.MapPost("/requests", async (
            TenantTerminationRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationRequest,
                    DataRightsAdminPermissions.TenantTerminationRequest),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RequestTenantTerminationCommand(
                            request.RequestId,
                            request.ExportRequested,
                            request.RequesterRelationship,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationCaseDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationCaseDto>();

        group.MapPost("/{caseId:guid}/decision", async (
            Guid caseId,
            TenantTerminationDecisionRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationDecide,
                    DataRightsAdminPermissions.TenantTerminationApprove),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new DecideTenantTerminationCommand(
                            caseId,
                            request.Decision,
                            request.Reason,
                            request.ApprovalEvidence,
                            request.ExpectedVersion,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationCaseDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationCaseDto>();

        group.MapPost("/{caseId:guid}/processes/{processId:guid}/start", async (
            Guid caseId,
            Guid processId,
            TenantTerminationStartRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationStart,
                    DataRightsAdminPermissions.TenantTerminationExecute),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new StartTenantTerminationCommand(
                            caseId,
                            processId,
                            request.ApprovalEvidence,
                            request.ExpectedCaseVersion,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationStartDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationStartDto>();

        group.MapPost(
            "/{caseId:guid}/processes/{processId:guid}/export/{artifactId:guid}/confirm",
            async (
                Guid caseId,
                Guid processId,
                Guid artifactId,
                TenantTerminationExportConfirmationRequest request,
                HttpContext context,
                AdminApiExecutor executor,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                ApplySensitiveResponseHeaders(context.Response);
                return await executor.ExecuteAsync(
                    context,
                    AdminOperation.Create(
                        DataRightsAdminOperationNames
                            .TenantTerminationExportConfirm,
                        DataRightsAdminPermissions
                            .TenantTerminationExportConfirm),
                    requireTenant: true,
                    token => request.Confirmed
                        ? dispatcher.SendAsync(
                            new ConfirmTenantTerminationExportCommand(
                                caseId,
                                processId,
                                artifactId,
                                request.ExportOperationRevision,
                                request.ExpectedProcessVersion,
                                request.ExpectedArtifactVersion,
                                request.FrozenRevisionSha256,
                                request.FragmentSetSha256,
                                Actor(context)),
                            token)
                        : Task.FromResult(Result.Failure<
                            TenantTerminationProcessDto>(
                                AdminErrors.ConfirmationRequired)),
                    cancellationToken,
                    errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
            })
            .Produces<TenantTerminationProcessDto>();

        group.MapPost("/processes/{processId:guid}/retry", async (
            Guid processId,
            TenantTerminationProcessVersionRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationRetry,
                    DataRightsAdminPermissions.TenantTerminationRetry),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RetryTenantTerminationCommand(
                            processId,
                            request.ExpectedProcessVersion,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationProcessDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationProcessDto>();

        group.MapPost("/processes/{processId:guid}/cancel", async (
            Guid processId,
            TenantTerminationProcessVersionRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationCancel,
                    DataRightsAdminPermissions.TenantTerminationCancel),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RequestTenantTerminationCancellationCommand(
                            processId,
                            request.ExpectedProcessVersion,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationProcessDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationProcessDto>();

        group.MapPost("/{caseId:guid}/processes/{processId:guid}/recover", async (
            Guid caseId,
            Guid processId,
            TenantTerminationRecoveryRequest request,
            HttpContext context,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            ApplySensitiveResponseHeaders(context.Response);
            return await executor.ExecuteAsync(
                context,
                AdminOperation.Create(
                    DataRightsAdminOperationNames.TenantTerminationRecover,
                    DataRightsAdminPermissions.TenantTerminationRecover),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RecoverTenantTerminationCommand(
                            caseId,
                            processId,
                            request.ApprovalEvidence,
                            request.ExpectedCaseVersion,
                            request.ExpectedProcessVersion,
                            Actor(context)),
                        token)
                    : Task.FromResult(Result.Failure<TenantTerminationStartDto>(
                        AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<TenantTerminationStartDto>();
    }

    private static string Actor(HttpContext context)
    {
        string identity = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.Identity?.Name
            ?? $"authenticated:{context.User.Identity?.AuthenticationType ?? "unknown"}";
        return $"admin-api:{identity}";
    }

    private static void ApplySensitiveResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private static void ApplyDownloadResponseHeaders(HttpResponse response)
    {
        ApplySensitiveResponseHeaders(response);
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "sandbox";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    private static readonly ApiErrorStatusCodeMap ErrorStatusCodes =
        ApiErrorStatusCodeMap.Create(
        [
            new(DataRightsApplicationErrors.TenantTerminationCaseNotFound.Code,
                StatusCodes.Status404NotFound),
            new(DataRightsApplicationErrors.TenantTerminationProcessNotFound.Code,
                StatusCodes.Status404NotFound),
            new(DataRightsApplicationErrors.TenantTerminationExportArtifactNotFound.Code,
                StatusCodes.Status404NotFound),
            new(DataRightsApplicationErrors.TenantTerminationExportArtifactExpired.Code,
                StatusCodes.Status410Gone),
            new(DataRightsApplicationErrors.TenantTerminationContributorCatalogInvalid.Code,
                StatusCodes.Status503ServiceUnavailable),
            new(DataRightsApplicationErrors.TenantTerminationReplayIntentInvalid.Code,
                StatusCodes.Status503ServiceUnavailable),
            new(DataRightsApplicationErrors.TenantTerminationActiveCaseExists.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationRequestConflict.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationDecisionConflict.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationApprovalEvidenceInvalid.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationStartConflict.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationRecoveryRequiresRetry.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationExecutionStateInvalid.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationCancellationProofInvalid.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationExportArtifactNotAvailable.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationExportVerificationFailed.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TenantTerminationExportConfirmationInvalid.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.VersionConflict.Code,
                StatusCodes.Status409Conflict),
            new(DataRightsApplicationErrors.TransitionInvalid.Code,
                StatusCodes.Status409Conflict)
        ]);

    internal sealed record TenantTerminationRequest(
        Guid RequestId,
        bool ExportRequested,
        DataRightsRequesterRelationship RequesterRelationship,
        bool Confirmed);

    internal sealed record TenantTerminationDecisionRequest(
        DataRightsDecisionOutcome Decision,
        DataRightsDecisionReason Reason,
        TenantTerminationApprovalEvidence? ApprovalEvidence,
        long ExpectedVersion,
        bool Confirmed);

    internal sealed record TenantTerminationStartRequest(
        TenantTerminationApprovalEvidence ApprovalEvidence,
        long ExpectedCaseVersion,
        bool Confirmed);

    internal sealed record TenantTerminationProcessVersionRequest(
        long ExpectedProcessVersion,
        bool Confirmed);

    internal sealed record TenantTerminationExportConfirmationRequest(
        long ExportOperationRevision,
        long ExpectedProcessVersion,
        long ExpectedArtifactVersion,
        string FrozenRevisionSha256,
        string FragmentSetSha256,
        bool Confirmed);

    internal sealed record TenantTerminationRecoveryRequest(
        TenantTerminationApprovalEvidence ApprovalEvidence,
        long ExpectedCaseVersion,
        long? ExpectedProcessVersion,
        bool Confirmed);
}
