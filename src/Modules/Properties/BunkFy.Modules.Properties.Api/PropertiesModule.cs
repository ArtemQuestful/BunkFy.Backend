namespace BunkFy.Modules.Properties.Api;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.DataGovernance;
using Gma.Framework.Runtime.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;

public sealed class PropertiesModule : IModule
{
    public string Name => PropertiesModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        bool previewDefaults = builder.Configuration.GetValue<bool>(PreviewCountryPolicyDefaults.EnabledConfigurationKey);
        bool autoActivate = builder.Configuration.GetValue<bool>(PreviewCountryPolicyDefaults.AutoActivateConfigurationKey);
        if ((previewDefaults || autoActivate) && !builder.Environment.IsEnvironment("Preview"))
        {
            throw new InvalidOperationException("Automatic test country policies are available only in the Preview environment.");
        }

        if (autoActivate && !previewDefaults)
        {
            throw new InvalidOperationException("Automatic property activation requires Preview test country policies.");
        }

        builder.Services.TryAddSingleton(services => new PreviewNewPropertyProcessing(
            autoActivate, services.GetRequiredService<CountryPolicyRegistry>()));
        builder.SelectModuleProfile(PropertiesProfiles.Default, "BunkFy.Modules.Properties.Api");
        builder.Services.AddOptions<PropertiesApiSecurityOptions>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, PropertiesNoStoreStartupFilter>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAccessHttpScopeResolver, PropertyAccessScopeResolver>());
        builder.Services.AddPropertiesApplication();
        builder.AddPropertiesPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        PropertiesApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<PropertiesApiSecurityOptions>>()
            .Value;
        PreviewNewPropertyProcessing? previewDefaults = endpoints.ServiceProvider.GetService<PreviewNewPropertyProcessing>();
        if (endpoints.ServiceProvider.GetService<IConfiguration>()?
                .GetValue<bool>(PreviewCountryPolicyDefaults.AutoActivateConfigurationKey) == true &&
            previewDefaults?.Enabled != true)
        {
            throw new InvalidOperationException("Preview activation was configured without its module services.");
        }
        RouteGroupBuilder properties = endpoints.MapGroup("/api/properties")
            .WithModuleName(this.Name)
            .WithTags("Properties")
            .RequireAuthorization();
        properties.AddEndpointFilter(SensitiveResponseFilter);

        properties.MapGet("/", async (
            int? page,
            int? pageSize,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            AccessSubject? subject = subjectResolver.ResolveSubject(httpContext);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            Result<PropertyListResponse> result = await dispatcher.QueryAsync(
                new ListVisiblePropertiesQuery(
                    subject,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false);

            return result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<PropertyListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant();

        properties.MapGet("/time-zones/catalog", async (
            string? search,
            string? countryCode,
            string? cursor,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListPropertyTimeZoneCatalogQuery(
                    search,
                    countryCode,
                    cursor,
                    pageSize ?? 50),
                cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyTimeZoneCatalogPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireTenantPermission(PropertiesAdminPermissionCodes.Read);

        properties.MapGet("/{propertyId:guid}/time-zones/catalog", async (
            Guid propertyId,
            string? search,
            string? countryCode,
            string? cursor,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListPropertyTimeZoneCatalogQuery(
                    search,
                    countryCode,
                    cursor,
                    pageSize ?? 50),
                cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyTimeZoneCatalogPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(
                PropertiesAdminPermissionCodes.Read,
                PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/time-zones/compliance", async (
            string? cursor,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListPropertyTimeZoneComplianceQuery(cursor, pageSize ?? 50),
                cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyTimeZoneCompliancePageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireTenantPermission(PropertiesAdminPermissionCodes.TimeZonesManage);

        properties.MapGet("/{propertyId:guid}/country-policies", async (
            Guid propertyId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(new ListCountryPoliciesQuery(), cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<CountryPolicyListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.PropertiesManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/{propertyId:guid}", async (
            Guid propertyId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(new GetPropertyQuery(propertyId), cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.Read, PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/{propertyId:guid}/time-zone/operations/{operationId:guid}", async (
            Guid propertyId,
            Guid operationId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetPropertyTimeZoneRecoveryQuery(propertyId, operationId),
                cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyTimeZoneRecoveryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(
                PropertiesAdminPermissionCodes.TimeZonesManage,
                PropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder setPropertyTimeZone = properties.MapPut("/{propertyId:guid}/time-zone", async (
            Guid propertyId,
            SetPropertyTimeZoneRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            if (actorId is null)
            {
                return Results.Unauthorized();
            }

            Result<SetPropertyTimeZoneReceiptDto> result = await dispatcher.SendAsync(
                new SetPropertyTimeZoneCommand(
                    propertyId,
                    request.OperationId,
                    request.TimeZoneId,
                    request.Confirmed,
                    request.ExpectedVersion,
                    actorId),
                cancellationToken).ConfigureAwait(false);
            return result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<SetPropertyTimeZoneReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(
                PropertiesAdminPermissionCodes.TimeZonesManage,
                PropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            setPropertyTimeZone,
            security.TimeZoneManagementAssurance);

        properties.MapGet("/{propertyId:guid}/processing", async (
            Guid propertyId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetPropertyProcessingStateQuery(propertyId),
                cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyProcessingStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.Read, PropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder createProperty = properties.MapPost("/", async (
            PropertyCreateRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            if (actorId is null)
            {
                return Results.Unauthorized();
            }

            Result<PropertyMutationReceiptDto> creation = await dispatcher.SendAsync(
                new CreatePropertyCommand(
                    request.OperationId,
                    request.Name,
                    request.Code,
                    request.TimeZoneId,
                    actorId),
                cancellationToken).ConfigureAwait(false);
            if (previewDefaults?.Enabled != true)
            {
                return creation.ToHttpResult(PublicErrorStatusCodes);
            }

            return (await previewDefaults.ApplyAsync(
                creation, request.TimeZoneId, actorId, dispatcher,
                httpContext.RequestServices.GetRequiredService<ISystemClock>().UtcNow,
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireTenantPermission(PropertiesAdminPermissionCodes.PropertiesManage);
        if (previewDefaults?.Enabled == true)
        {
            RequireAssuranceWhenConfigured(createProperty, security.ProcessingActivationAssurance);
        }

        properties.MapPut("/{propertyId:guid}", async (
            Guid propertyId,
            PropertyUpdateRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new UpdatePropertyCommand(
                    propertyId,
                    request.OperationId,
                    request.Name,
                    request.Code,
                    request.TimeZoneId,
                    request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.PropertiesManage, PropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder retireProperty = properties.MapPost("/{propertyId:guid}/retire", async (
            Guid propertyId,
            RetirePropertyRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            if (actorId is null)
            {
                return Results.Unauthorized();
            }

            Result<PropertyMutationReceiptDto> result =
                await dispatcher.SendAsync(
                    new RetirePropertyCommand(
                        propertyId,
                        request.OperationId,
                        request.Confirmed,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false);
            return result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.PropertiesManage, PropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            retireProperty,
            security.PropertyRetirementAssurance);

        RouteHandlerBuilder activateProcessing = properties.MapPost("/{propertyId:guid}/processing/activate", async (
            Guid propertyId,
            ActivatePropertyProcessingRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            if (actorId is null)
            {
                return Results.Unauthorized();
            }

            Result<PropertyMutationReceiptDto> result = await dispatcher.SendAsync(
                new ActivatePropertyProcessingCommand(
                    propertyId,
                    request.OperationId,
                    request.OperatingCountryCode,
                    request.PolicyId,
                    request.PolicyVersion,
                    request.DataRegionId,
                    request.TransferProfileId,
                    request.RetentionPolicyId,
                    request.RetentionPolicyVersion,
                    request.AcceptedAcknowledgements,
                    request.Confirmed,
                    request.ExpectedVersion,
                    actorId),
                cancellationToken).ConfigureAwait(false);
            return result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.PropertiesManage, PropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            activateProcessing,
            security.ProcessingActivationAssurance);

        properties.MapPost("/{propertyId:guid}/processing/suspend", async (
            Guid propertyId,
            SuspendPropertyProcessingRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            if (actorId is null)
            {
                return Results.Unauthorized();
            }

            Result<PropertyMutationReceiptDto> result =
                await dispatcher.SendAsync(
                    new SuspendPropertyProcessingCommand(
                        propertyId,
                        request.OperationId,
                        request.Confirmed,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false);
            return result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.PropertiesManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/{propertyId:guid}/rooms", async (
            Guid propertyId,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListRoomsQuery(propertyId, page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.Read, PropertyAccessScopeResolver.ResolverName);

        properties.MapPost("/{propertyId:guid}/rooms", async (
            Guid propertyId,
            RoomCreateRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new CreateRoomCommand(
                    request.OperationId,
                    propertyId,
                    request.ExpectedPropertyVersion,
                    request.Name,
                    request.BuildingLabel,
                    request.FloorLabel),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.RoomsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/{propertyId:guid}/rooms/{roomId:guid}", async (
            Guid propertyId,
            Guid roomId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(new GetRoomQuery(propertyId, roomId), cancellationToken).ConfigureAwait(false))
            .ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.Read, PropertyAccessScopeResolver.ResolverName);

        properties.MapPut("/{propertyId:guid}/rooms/{roomId:guid}", async (
            Guid propertyId,
            Guid roomId,
            RoomUpdateRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new UpdateRoomCommand(
                    request.OperationId,
                    propertyId,
                    roomId,
                    request.ExpectedVersion,
                    request.Name,
                    request.BuildingLabel,
                    request.FloorLabel),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.RoomsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/retire", async (
            Guid propertyId,
            Guid roomId,
            RetireRoomRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            Result<Unit> result = request.Confirmed
                ? await dispatcher.SendAsync(
                    new RetireRoomCommand(propertyId, roomId, request.ExpectedVersion, request.CascadeBeds),
                    cancellationToken).ConfigureAwait(false)
                : Result.Failure<Unit>(new("Properties.ConfirmationRequired", "Confirmation is required."));

            return result.IsSuccess ? Results.NoContent() : result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces(StatusCodes.Status204NoContent)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.RoomsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapGet("/{propertyId:guid}/rooms/{roomId:guid}/beds", async (
            Guid propertyId,
            Guid roomId,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListBedsQuery(propertyId, roomId, page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.Read, PropertyAccessScopeResolver.ResolverName);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds", async (
            Guid propertyId,
            Guid roomId,
            BedWriteRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new AddBedCommand(
                    request.OperationId,
                    propertyId,
                    roomId,
                    request.ExpectedRoomVersion,
                    request.Label),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.BedsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds/batch", async (
            Guid propertyId,
            Guid roomId,
            BedBatchWriteRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new AddBedsCommand(
                    request.OperationId,
                    propertyId,
                    roomId,
                    request.ExpectedRoomVersion,
                    request.Labels),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedBatchMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.BedsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapPut("/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            BedWriteRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new UpdateBedCommand(
                    request.OperationId,
                    propertyId,
                    roomId,
                    bedId,
                    request.ExpectedRoomVersion,
                    request.Label),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.BedsManage, PropertyAccessScopeResolver.ResolverName);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}/retire", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            RetireBedRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            Result<Unit> result = request.Confirmed
                ? await dispatcher.SendAsync(
                    new RetireBedCommand(propertyId, roomId, bedId, request.ExpectedRoomVersion),
                    cancellationToken).ConfigureAwait(false)
                : Result.Failure<Unit>(new("Properties.ConfirmationRequired", "Confirmation is required."));

            return result.IsSuccess ? Results.NoContent() : result.ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces(StatusCodes.Status204NoContent)
            .RequireTenant()
            .RequireResolvedScopePermission(PropertiesAdminPermissionCodes.BedsManage, PropertyAccessScopeResolver.ResolverName);
    }

    public sealed record PropertyCreateRequest(
        Guid OperationId,
        string Name,
        string Code,
        string TimeZoneId);
    public sealed record PropertyUpdateRequest(
        Guid OperationId,
        string Name,
        string Code,
        string? TimeZoneId,
        long ExpectedVersion);
    public sealed record SetPropertyTimeZoneRequest(
        Guid OperationId,
        string TimeZoneId,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record RetirePropertyRequest(
        Guid OperationId,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record ActivatePropertyProcessingRequest(
        Guid OperationId,
        string OperatingCountryCode,
        string PolicyId,
        int PolicyVersion,
        string DataRegionId,
        string TransferProfileId,
        string RetentionPolicyId,
        int RetentionPolicyVersion,
        IReadOnlyCollection<PropertyGovernanceAcknowledgementDto> AcceptedAcknowledgements,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record SuspendPropertyProcessingRequest(
        Guid OperationId,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record RoomCreateRequest(
        Guid OperationId,
        string Name,
        long ExpectedPropertyVersion,
        string? BuildingLabel = null,
        string? FloorLabel = null);
    public sealed record RoomUpdateRequest(
        Guid OperationId,
        string Name,
        long ExpectedVersion,
        string? BuildingLabel = null,
        string? FloorLabel = null);
    public sealed record RetireRoomRequest(bool Confirmed, long ExpectedVersion, bool CascadeBeds = false);
    public sealed record BedWriteRequest(
        Guid OperationId,
        string Label,
        long ExpectedRoomVersion);
    public sealed record BedBatchWriteRequest(
        Guid OperationId,
        IReadOnlyCollection<string> Labels,
        long ExpectedRoomVersion);
    public sealed record RetireBedRequest(bool Confirmed, long ExpectedRoomVersion);

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

    private static string? ResolveActor(HttpContext context, IAccessHttpSubjectResolver subjectResolver)
    {
        AccessSubject? subject = subjectResolver.ResolveSubject(context);
        return subject is null
            ? null
            : $"{AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    private static RouteHandlerBuilder RequireAssuranceWhenConfigured(
        RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement) =>
        requirement is null
            ? endpoint
            : endpoint.RequireAuthenticationAssurance(requirement);

    private static readonly ApiErrorStatusCodeMap PublicErrorStatusCodes = CreateErrorStatusCodes(
        new(PropertiesApplicationErrors.AccessDenied.Code, StatusCodes.Status403Forbidden),
        new(PropertiesDomainErrors.ActorIdInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneTooLong.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.ConfirmationRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.TimeZoneQueryInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.CreationOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.ManagementOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.BedBatchRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.BedBatchTooLarge.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.PropertyNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.TimeZoneOperationNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.RoomNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.BedNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.PropertyCodeAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.ManagementOperationConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.TimeZoneDedicatedOperationRequired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyAlreadyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyProcessingNotEnabled.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.ProcessingLifecycleRestricted.Code, StatusCodes.Status423Locked),
        new(PropertiesApplicationErrors.ProcessingLifecycleAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.WorkspaceProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(PropertiesApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.TimeSourceUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.TimeZoneRuntimeUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.PropertyHasActiveRooms.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomHasActiveBeds.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedAlreadyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedRetirementRequiresInventory.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomRetirementRequiresInventory.Code, StatusCodes.Status409Conflict));

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(entries.Concat(
            PropertiesApplicationErrors.CountryPolicyDenials.Select(error =>
                new ApiErrorStatusCode(error.Code, StatusCodes.Status409Conflict))).ToArray());
}
