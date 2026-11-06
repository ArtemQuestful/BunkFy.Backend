namespace BunkFy.Modules.Staff.AdminApi;

using System.Security.Claims;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using BunkFy.Modules.Staff.Admin.Contracts;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Persistence;

public sealed class StaffAdminApiModule : IAdminApiModule
{
    public string Name => StaffModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(StaffProfiles.Default, "BunkFy.Modules.Staff.AdminApi");
        builder.Services.AddStaffApplication();
        builder.AddStaffPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/admin/staff/members")
            .WithModuleName(this.Name).WithTags("Staff Admin").RequireAuthorization();
        group.MapGet("", async (string? search, StaffStatus? status, int? page, int? pageSize,
            HttpContext context, AdminApiExecutor executor, IRequestDispatcher dispatcher,
            CancellationToken token) => await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.List, StaffAdminPermissions.Read), true,
                ct => dispatcher.QueryAsync(new ListStaffMembersQuery(search, status,
                    page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize), ct),
                token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffDirectoryListResponse>(StatusCodes.Status200OK);
        group.MapGet("/{staffMemberId:guid}", async (Guid staffMemberId, HttpContext context,
            AdminApiExecutor executor, IRequestDispatcher dispatcher, CancellationToken token) =>
        {
            MarkSensitiveResponse(context);
            return await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Get, StaffAdminPermissions.SensitiveProfileRead), true,
                ct => dispatcher.QueryAsync(new GetStaffMemberQuery(staffMemberId), ct), token,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<StaffMemberDto>(StatusCodes.Status200OK);
        group.MapPost("", async (StaffProfileWriteRequest request, HttpContext context,
            AdminApiExecutor executor, IRequestDispatcher dispatcher, CancellationToken token) =>
        {
            return await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Create, StaffAdminPermissions.Create), true,
                ct => dispatcher.SendAsync(new CreateStaffMemberCommand(request.OperationId,
                    request.DisplayName, request.LegalName, request.WorkEmail, request.WorkPhone,
                    request.EmployeeNumber, request.JobTitle, request.Department,
                    Actor(context)), ct),
                token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<StaffDirectoryMemberDto>(StatusCodes.Status200OK);
        group.MapPut("/{staffMemberId:guid}", async (Guid staffMemberId, StaffProfileUpdateRequest request,
            HttpContext context, AdminApiExecutor executor, IRequestDispatcher dispatcher,
            CancellationToken token) =>
        {
            return await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Update, StaffAdminPermissions.Manage), true,
                ct => dispatcher.SendAsync(new UpdateStaffMemberCommand(request.OperationId,
                    staffMemberId, request.DisplayName, request.LegalName, request.WorkEmail, request.WorkPhone,
                    request.EmployeeNumber, request.JobTitle, request.Department, request.ExpectedVersion,
                    Actor(context)), ct), token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPut("/{staffMemberId:guid}/auth-subject", async (Guid staffMemberId,
            StaffAuthSubjectRequest request, HttpContext context, AdminApiExecutor executor,
            IRequestDispatcher dispatcher, CancellationToken token) =>
        {
            return await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.SetAuthSubject,
                    StaffAdminPermissions.AccountLinksManage), true,
                ct => request.Confirmed ? dispatcher.SendAsync(new SetStaffAuthSubjectCommand(
                    request.OperationId,
                    staffMemberId,
                    request.AuthSubjectId,
                    request.ExpectedVersion,
                    Actor(context)), ct)
                    : Task.FromResult(Result.Failure<StaffMemberMutationReceiptDto>(
                        AdminErrors.ConfirmationRequired)),
                token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        }).Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPost("/{staffMemberId:guid}/suspend", async (Guid staffMemberId,
            StaffLifecycleRequest request, HttpContext context, AdminApiExecutor executor,
            IRequestDispatcher dispatcher, CancellationToken token) => await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Suspend, StaffAdminPermissions.ManageLifecycle), true,
                ct => dispatcher.SendAsync(new SuspendStaffMemberCommand(
                    request.OperationId, staffMemberId, request.Reason,
                    request.ExpectedVersion, Actor(context)), ct), token,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPost("/{staffMemberId:guid}/resume", async (Guid staffMemberId,
            StaffLifecycleRequest request, HttpContext context, AdminApiExecutor executor,
            IRequestDispatcher dispatcher, CancellationToken token) => await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Resume, StaffAdminPermissions.ManageLifecycle), true,
                ct => dispatcher.SendAsync(new ResumeStaffMemberCommand(
                    request.OperationId, staffMemberId, request.Reason,
                    request.ExpectedVersion, Actor(context)), ct), token,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPost("/{staffMemberId:guid}/depart", async (Guid staffMemberId,
            StaffDepartureRequest request, HttpContext context, AdminApiExecutor executor,
            IRequestDispatcher dispatcher, CancellationToken token) => await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.Depart, StaffAdminPermissions.ManageLifecycle), true,
                ct => request.Confirmed ? dispatcher.SendAsync(new DepartStaffMemberCommand(
                    request.OperationId, staffMemberId, request.EffectiveOn, request.Reason,
                    request.ExpectedVersion, Actor(context)), ct)
                    : Task.FromResult(Result.Failure<StaffMemberMutationReceiptDto>(
                        AdminErrors.ConfirmationRequired)),
                token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPut("/{staffMemberId:guid}/properties/{propertyId:guid}", async (Guid staffMemberId,
            Guid propertyId, StaffAssignmentRequest request, HttpContext context, AdminApiExecutor executor,
            IRequestDispatcher dispatcher, CancellationToken token) => await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.AssignProperty,
                    StaffAdminPermissions.AssignProperties), true,
                ct => dispatcher.SendAsync(new AssignStaffPropertyCommand(request.OperationId,
                    staffMemberId, propertyId,
                    request.PropertyJobTitle, request.IsPrimary, request.EffectiveFrom,
                    request.ExpectedVersion, Actor(context)), ct), token,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
        group.MapPost("/{staffMemberId:guid}/properties/{propertyId:guid}/unassign", async (
            Guid staffMemberId, Guid propertyId, StaffUnassignmentRequest request, HttpContext context,
            AdminApiExecutor executor, IRequestDispatcher dispatcher, CancellationToken token) =>
            await executor.ExecuteAsync(context,
                AdminOperation.Create(StaffAdminOperationNames.UnassignProperty,
                    StaffAdminPermissions.AssignProperties), true,
                ct => dispatcher.SendAsync(new UnassignStaffPropertyCommand(request.OperationId,
                    staffMemberId, propertyId,
                    request.EffectiveTo, request.Reason, request.ExpectedVersion, Actor(context)), ct),
                token, errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<StaffMemberMutationReceiptDto>(StatusCodes.Status200OK);
    }

    public sealed record StaffProfileWriteRequest(Guid OperationId, string DisplayName, string? LegalName,
        string? WorkEmail, string? WorkPhone, string? EmployeeNumber, string? JobTitle,
        string? Department);
    public sealed record StaffProfileUpdateRequest(Guid OperationId, string DisplayName, string? LegalName,
        string? WorkEmail, string? WorkPhone, string? EmployeeNumber, string? JobTitle,
        string? Department, long ExpectedVersion);
    public sealed record StaffAuthSubjectRequest(Guid OperationId, string? AuthSubjectId,
        long ExpectedVersion, bool Confirmed);
    public sealed record StaffLifecycleRequest(Guid OperationId, string Reason,
        long ExpectedVersion);
    public sealed record StaffDepartureRequest(Guid OperationId, DateOnly EffectiveOn,
        string Reason, long ExpectedVersion, bool Confirmed);
    public sealed record StaffAssignmentRequest(Guid OperationId, string? PropertyJobTitle, bool IsPrimary,
        DateOnly EffectiveFrom, long ExpectedVersion);
    public sealed record StaffUnassignmentRequest(Guid OperationId, DateOnly EffectiveTo, string Reason,
        long ExpectedVersion);

    private static string Actor(HttpContext context)
    {
        string identity = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.Identity?.Name
            ?? $"authenticated:{context.User.Identity?.AuthenticationType ?? "unknown"}";
        return $"admin-api:{identity}";
    }

    private static void MarkSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static readonly ApiErrorStatusCodeMap ErrorStatusCodes = ApiErrorStatusCodeMap.Create(
        new(StaffApplicationErrors.StaffMemberNotFound.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.AssignmentNotFound.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.PropertyUnavailable.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.EmployeeNumberConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectUnlinkRequiresSuspension.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectReplacementRequiresUnlink.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectLinkRequiresActive.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.ProfileUpdateOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.IdentityAnchorResolutionRequired.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.IdentityAnchorAccessClosureRequired.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.LifecycleOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AssignmentOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AssignmentAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.PrimaryAssignmentExists.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.StaffSuspended.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.StaffDeparted.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CreationOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.ProfileUpdateOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.AuthSubjectOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.LifecycleOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.AssignmentOperationInvalid.Code, StatusCodes.Status400BadRequest));
}
