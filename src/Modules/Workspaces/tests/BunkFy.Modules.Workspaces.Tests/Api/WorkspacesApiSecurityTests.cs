namespace BunkFy.Modules.Workspaces.Tests.Api;

using System.Reflection;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.AdminApi;
using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Workspaces.Api.Requests;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Security;
using Gma.Modules.AccessControl.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesApiSecurityTests
{
    [Fact]
    public void Profile_mutation_authority_failure_maps_to_conflict()
    {
        Type apiSupport = typeof(WorkspacesModule).Assembly.GetType(
            "BunkFy.Modules.Workspaces.Api.WorkspacesApiEndpointSupport",
            throwOnError: true)!;
        ApiErrorStatusCodeMap mappings =
            (ApiErrorStatusCodeMap)apiSupport.GetField(
                "ErrorStatusCodes",
                BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(
            StatusCodes.Status409Conflict,
            mappings.GetStatusCode(
                WorkspaceStaffOnboardingApplicationErrors
                    .ProfileMutationAuthorityUnavailable));
    }

    [Fact]
    public void Sensitive_response_policies_disable_storage()
    {
        Type apiSupport = typeof(WorkspacesModule).Assembly.GetType(
            "BunkFy.Modules.Workspaces.Api.WorkspacesApiEndpointSupport",
            throwOnError: true)!;
        MethodInfo apiPolicy = apiSupport.GetMethod(
            "SetNoStore",
            BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo adminPolicy = typeof(WorkspacesAdminApiModule).GetMethod(
            "MarkSensitiveResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        DefaultHttpContext apiContext = new();
        DefaultHttpContext adminContext = new();
        apiPolicy.Invoke(null, [apiContext]);
        adminPolicy.Invoke(null, [adminContext]);

        AssertNoStore(apiContext);
        AssertNoStore(adminContext);
    }

    [Fact]
    public async Task Operator_onboarding_routes_require_workspace_authority_and_assurance()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<WorkspacesApiSecurityOptions>(options =>
        {
            options.StaffOnboardingManagementAssurance =
                new AuthenticationAssuranceRequirement(
                    maxAuthenticationAge: TimeSpan.FromMinutes(10));
            options.StaffOnboardingRecoveryPostEnabled = true;
        });
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/invitations",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceIssuanceDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/enrollment-links",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceIssuanceDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources",
            HttpMethods.Get,
            typeof(WorkspaceStaffJoinSourceListResponse),
            assuranceExpected: false);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/invitations/{sourceId:guid}/revoke",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/enrollment-links/{sourceId:guid}/disable",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/invitations/{sourceId:guid}/replace",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceReplacementDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/sources/enrollment-links/{sourceId:guid}/replace",
            HttpMethods.Post,
            typeof(WorkspaceStaffJoinSourceReplacementDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/applications",
            HttpMethods.Get,
            typeof(WorkspaceStaffOnboardingListResponse),
            assuranceExpected: false);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/retry",
            HttpMethods.Post,
            typeof(WorkspaceStaffOnboardingDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/recovery-attempts",
            HttpMethods.Post,
            typeof(WorkspaceStaffOnboardingRecoveryAttemptDto),
            assuranceExpected: true);
        AssertProtectedOnboardingRoute(
            endpoints,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/recovery-attempts/latest",
            HttpMethods.Get,
            typeof(WorkspaceStaffOnboardingRecoveryAttemptDto),
            assuranceExpected: false);
    }

    [Theory]
    [InlineData(
        WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered,
        StatusCodes.Status200OK,
        false)]
    [InlineData(
        WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired,
        StatusCodes.Status409Conflict,
        false)]
    [InlineData(
        WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
        StatusCodes.Status503ServiceUnavailable,
        true)]
    public async Task Recovery_endpoint_maps_typed_outcome_and_server_actor(
        WorkspaceStaffOnboardingRecoveryAttemptOutcome outcome,
        int expectedStatus,
        bool retryAfterExpected)
    {
        Guid applicationId = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");
        Guid operationId = Guid.Parse(
            "10000000-0000-0000-0000-000000000002");
        WorkspaceStaffOnboardingRecoveryAttemptDto attempt = new(
            operationId,
            applicationId,
            outcome,
            outcome == WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered
                ? WorkspaceStaffOnboardingRecoveryAttemptReason.ExactStateRecovered
                : outcome == WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired
                    ? WorkspaceStaffOnboardingRecoveryAttemptReason.AccessConflict
                    : WorkspaceStaffOnboardingRecoveryAttemptReason.AccessUnavailable,
            outcome == WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered
                ? WorkspaceStaffOnboardingStatus.Completed
                : WorkspaceStaffOnboardingStatus.Failed,
            ApplicationVersion: 8,
            outcome == WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater
                ? WorkspaceStaffOnboardingRecoveryRetryDirective
                    .NewOperationAfterDelay
                : WorkspaceStaffOnboardingRecoveryRetryDirective.None,
            outcome == WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater
                ? WorkspaceStaffOnboardingRecoveryAttemptDto
                    .RetryLaterDelaySeconds
                : null,
            new DateTimeOffset(2026, 8, 25, 20, 0, 0, TimeSpan.Zero));
        RecoveryDispatcher dispatcher = new(attempt);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<WorkspacesApiSecurityOptions>(options =>
            options.StaffOnboardingRecoveryPostEnabled = true);
        builder.Services.AddSingleton<IRequestDispatcher>(dispatcher);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(
            new FixedSubjectResolver());
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);
        RouteEndpoint endpoint = FindEndpoint(
            app,
            HttpMethods.Post,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/recovery-attempts");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new WorkspaceStaffOnboardingRecoveryAttemptRequest(
                operationId,
                ExpectedApplicationVersion: 7),
            JsonSerializerOptions.Web);
        DefaultHttpContext context = new()
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Request.RouteValues["applicationId"] = applicationId.ToString("D");
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new RequestBodyDetectionFeature());

        await endpoint.RequestDelegate!(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(
            retryAfterExpected,
            context.Response.Headers.ContainsKey("Retry-After"));
        if (retryAfterExpected)
        {
            Assert.Equal("5", context.Response.Headers.RetryAfter);
        }
        AttemptWorkspaceStaffOnboardingRecoveryCommand sent =
            Assert.IsType<AttemptWorkspaceStaffOnboardingRecoveryCommand>(
                dispatcher.LastCommand);
        Assert.Equal("auth:recovery-operator", sent.RecoveryActorId);
        Assert.Equal(applicationId, sent.ApplicationId);
        Assert.Equal(operationId, sent.OperationId);
        Assert.Equal(7, sent.ExpectedApplicationVersion);
    }

    [Fact]
    public async Task Recovery_post_is_default_off_without_dispatch_and_no_store()
    {
        Guid applicationId = Guid.Parse(
            "10000000-0000-0000-0000-000000000021");
        Guid operationId = Guid.Parse(
            "10000000-0000-0000-0000-000000000022");
        WorkspaceStaffOnboardingRecoveryAttemptDto attempt = new(
            operationId,
            applicationId,
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryAttemptReason.ExactStateRecovered,
            WorkspaceStaffOnboardingStatus.Completed,
            ApplicationVersion: 8,
            WorkspaceStaffOnboardingRecoveryRetryDirective.None,
            RetryAfterSeconds: null,
            new DateTimeOffset(2026, 8, 25, 20, 2, 0, TimeSpan.Zero));
        RecoveryDispatcher dispatcher = new(attempt);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<WorkspacesApiSecurityOptions>(_ => { });
        builder.Services.AddSingleton<IRequestDispatcher>(dispatcher);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(
            new FixedSubjectResolver());
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);
        RouteEndpoint endpoint = FindEndpoint(
            app,
            HttpMethods.Post,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/recovery-attempts");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new WorkspaceStaffOnboardingRecoveryAttemptRequest(
                operationId,
                ExpectedApplicationVersion: 7),
            JsonSerializerOptions.Web);
        DefaultHttpContext context = new()
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Request.RouteValues["applicationId"] =
            applicationId.ToString("D");
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new RequestBodyDetectionFeature());

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status404NotFound,
            context.Response.StatusCode);
        Assert.Null(dispatcher.LastCommand);
        AssertNoStore(context);
    }

    [Fact]
    public async Task Latest_recovery_attempt_route_returns_opaque_support_contract()
    {
        Guid applicationId = Guid.Parse(
            "10000000-0000-0000-0000-000000000011");
        WorkspaceStaffOnboardingRecoveryAttemptDto attempt = new(
            Guid.Parse("10000000-0000-0000-0000-000000000012"),
            applicationId,
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryAttemptReason.StaffConflict,
            WorkspaceStaffOnboardingStatus.Failed,
            ApplicationVersion: 4,
            WorkspaceStaffOnboardingRecoveryRetryDirective.None,
            RetryAfterSeconds: null,
            new DateTimeOffset(2026, 8, 25, 20, 1, 0, TimeSpan.Zero));
        RecoveryDispatcher dispatcher = new(attempt);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<WorkspacesApiSecurityOptions>(_ => { });
        builder.Services.AddSingleton<IRequestDispatcher>(dispatcher);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);
        RouteEndpoint endpoint = FindEndpoint(
            app,
            HttpMethods.Get,
            "/api/workspace-staff-enrollment/applications/{applicationId:guid}/recovery-attempts/latest");
        DefaultHttpContext context = new()
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["applicationId"] = applicationId.ToString("D");

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.IsType<GetLatestWorkspaceStaffOnboardingRecoveryAttemptQuery>(
            dispatcher.LastQuery);
        Assert.Null(dispatcher.LastCommand);
        AssertNoStore(context);
    }

    [Fact]
    public async Task Workspace_access_routes_use_read_and_manage_permissions()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/catalogue",
            HttpMethods.Get,
            AccessControlProfilePermissionCodes.Read,
            typeof(WorkspaceAccessCatalogueDto),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/profiles",
            HttpMethods.Get,
            AccessControlProfilePermissionCodes.Read,
            typeof(WorkspaceAccessProfileListResponse),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/profiles",
            HttpMethods.Post,
            AccessControlProfilePermissionCodes.Manage,
            typeof(WorkspaceAccessProfileDto),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/profiles/{profileId:guid}",
            HttpMethods.Put,
            AccessControlProfilePermissionCodes.Manage,
            typeof(WorkspaceAccessProfileDto),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/profiles/{profileId:guid}/archive",
            HttpMethods.Post,
            AccessControlProfilePermissionCodes.Manage,
            type: typeof(void),
            statusCode: StatusCodes.Status204NoContent);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/members/{subjectId}/access",
            HttpMethods.Get,
            AccessControlProfilePermissionCodes.Read,
            typeof(WorkspaceMemberAccessDto),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-access/members/{subjectId}/access",
            HttpMethods.Put,
            AccessControlProfilePermissionCodes.Assign,
            typeof(WorkspaceMemberAccessDto),
            StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Data_rights_correction_routes_require_tenant_execution_permission()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceIssuer>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffJoinSourceReplacementManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceAccessProfileManager>(_ => null!);
        builder.Services.AddSingleton<IWorkspaceMemberAccessManager>(_ => null!);
        builder.Services.AddSingleton<IScopeContextAccessor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertProtectedRoute(
            endpoints,
            "/api/workspace-staff-enrollment/data-rights-corrections/{applicationId:guid}",
            HttpMethods.Get,
            DataRightsAdminPermissionCodes.Execute,
            typeof(WorkspaceStaffOnboardingDataRightsCorrectionTargetDto),
            StatusCodes.Status200OK);
        AssertProtectedRoute(
            endpoints,
            "/api/workspace-staff-enrollment/data-rights-corrections",
            HttpMethods.Post,
            DataRightsAdminPermissionCodes.Execute,
            typeof(WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto),
            StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Admin_operational_routes_publish_explicit_response_contracts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<AdminApiExecutor>(_ => null!);
        await using WebApplication app = builder.Build();
        new WorkspacesAdminApiModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertAdminResponse<WorkspaceAccessBootstrapStatus>(
            endpoints,
            HttpMethods.Get,
            "/api/admin/workspaces/access-bootstrap");
        AssertAdminResponse<WorkspaceAccessBootstrapResult>(
            endpoints,
            HttpMethods.Post,
            "/api/admin/workspaces/access-bootstrap");
        AssertAdminResponse<WorkspaceStaffAccessProcessListResponse>(
            endpoints,
            HttpMethods.Get,
            "/api/admin/workspaces/staff-access-processes");
        AssertAdminResponse<WorkspaceStaffAccessProcessDto>(
            endpoints,
            HttpMethods.Post,
            "/api/admin/workspaces/staff-access-processes/{processId:guid}/retry");
    }

    private static void AssertProtectedOnboardingRoute(
        IEnumerable<RouteEndpoint> endpoints,
        string route,
        string method,
        Type responseType,
        bool assuranceExpected)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);
        AccessPermissionMetadata[] permissions = endpoint.Metadata
            .OfType<AccessPermissionMetadata>()
            .OrderBy(metadata => metadata.Permission.Value, StringComparer.Ordinal)
            .ToArray();
        IProducesResponseTypeMetadata response = Assert.Single(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == StatusCodes.Status200OK);

        Assert.Equal(
            [
                AccessControlProfilePermissionCodes.Read,
                WorkspacesPermissionCodes.StaffOnboardingManage
            ],
            permissions.Select(permission => permission.Permission.Value));
        Assert.All(
            permissions,
            permission => Assert.Equal("tenant", permission.ScopeResolverName));
        Assert.Equal(responseType, response.Type);
        Assert.Equal(
            assuranceExpected,
            endpoint.Metadata.Any(metadata => string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal)));
    }

    private static void AssertNoStore(HttpContext context)
    {
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }

    private static RouteEndpoint FindEndpoint(
        WebApplication app,
        string method,
        string route) => Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => string.Equals(
                    candidate.RoutePattern.RawText?.Trim('/'),
                    route.Trim('/'),
                    StringComparison.Ordinal) &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?
                    .HttpMethods.Contains(method, StringComparer.Ordinal) == true);

    private sealed class FixedSubjectResolver : IAccessHttpSubjectResolver
    {
        public AccessSubject ResolveSubject(HttpContext httpContext) =>
            AccessSubject.User("auth:recovery-operator");
    }

    private sealed class RecoveryDispatcher(
        WorkspaceStaffOnboardingRecoveryAttemptDto attempt)
        : IRequestDispatcher
    {
        public object? LastCommand { get; private set; }
        public object? LastQuery { get; private set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.LastCommand = command;
            return Task.FromResult((Result<TResponse>)(object)
                Result.Success(attempt));
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default)
        {
            this.LastQuery = query;
            return Task.FromResult((Result<TResponse>)(object)
                Result.Success(attempt));
        }
    }

    private sealed class RequestBodyDetectionFeature
        : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static void AssertAdminResponse<TResponse>(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);
        IProducesResponseTypeMetadata response = Assert.Single(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == StatusCodes.Status200OK);

        Assert.Equal(typeof(TResponse), response.Type);
    }

    private static void AssertProtectedRoute(
        IEnumerable<RouteEndpoint> endpoints,
        string route,
        string method,
        string permissionCode,
        Type? type,
        int statusCode)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);
        AccessPermissionMetadata permission = Assert.Single(
            endpoint.Metadata.OfType<AccessPermissionMetadata>());
        IProducesResponseTypeMetadata response = Assert.Single(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == statusCode);

        Assert.Equal(permissionCode, permission.Permission.Value);
        Assert.Equal("tenant", permission.ScopeResolverName);
        Assert.Equal(type, response.Type);
    }
}
