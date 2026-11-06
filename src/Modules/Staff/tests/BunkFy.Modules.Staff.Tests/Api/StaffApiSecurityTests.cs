namespace BunkFy.Modules.Staff.Tests.Api;

using System.CommandLine;
using System.Reflection;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.AdminApi;
using BunkFy.Modules.Staff.AdminCli;
using BunkFy.Modules.Staff.Api;
using BunkFy.Modules.Staff.Api.Requests;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffApiSecurityTests
{
    [Theory]
    [InlineData(typeof(StaffProfileWriteRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffProfileWriteRequest))]
    [InlineData(typeof(BunkFy.Modules.Staff.Api.Requests.StaffProfileUpdateRequest))]
    [InlineData(typeof(StaffSelfProfileUpdateRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffProfileUpdateRequest))]
    [InlineData(typeof(BunkFy.Modules.Staff.Api.Requests.StaffLifecycleRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffLifecycleRequest))]
    [InlineData(typeof(BunkFy.Modules.Staff.Api.Requests.StaffDepartureRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffDepartureRequest))]
    [InlineData(typeof(BunkFy.Modules.Staff.Api.Requests.StaffAssignmentRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffAssignmentRequest))]
    [InlineData(typeof(BunkFy.Modules.Staff.Api.Requests.StaffUnassignmentRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffUnassignmentRequest))]
    public void Member_mutation_requests_require_caller_owned_operation_identity(
        Type requestType)
    {
        PropertyInfo operationId = requestType.GetProperty("OperationId")!;
        ConstructorInfo constructor = Assert.Single(requestType.GetConstructors());
        ParameterInfo parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => string.Equals(
                candidate.Name,
                "operationId",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(Guid), operationId.PropertyType);
        Assert.Equal(typeof(Guid), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
        Assert.Null(requestType.GetProperty("ActorId"));
    }

    [Fact]
    public void Self_service_profile_request_excludes_management_owned_fields()
    {
        Type request = typeof(StaffSelfProfileUpdateRequest);

        Assert.Null(request.GetProperty("EmployeeNumber"));
        Assert.NotNull(request.GetProperty("DisplayName"));
        Assert.NotNull(request.GetProperty("LegalName"));
        Assert.NotNull(request.GetProperty("WorkEmail"));
        Assert.NotNull(request.GetProperty("WorkPhone"));
        Assert.NotNull(request.GetProperty("JobTitle"));
        Assert.NotNull(request.GetProperty("Department"));
    }

    [Theory]
    [InlineData(typeof(StaffProfileWriteRequest))]
    [InlineData(typeof(StaffAdminApiModule.StaffProfileWriteRequest))]
    [InlineData(typeof(CreateStaffMemberCommand))]
    public void Manual_staff_creation_excludes_auth_subject_correlation(
        Type createType) =>
        Assert.Null(createType.GetProperty("AuthSubjectId"));

    [Theory]
    [InlineData(typeof(ProvisionStaffOnboardingCommand))]
    [InlineData(typeof(BootstrapStaffIdentityCommand))]
    [InlineData(typeof(StaffOnboardingProvisioningRequest))]
    [InlineData(typeof(StaffIdentityBootstrapRequest))]
    public void Trusted_identity_paths_retain_explicit_auth_subject_correlation(
        Type trustedType) =>
        Assert.Equal(
            typeof(string),
            trustedType.GetProperty("AuthSubjectId")?.PropertyType);

    [Fact]
    public void Unsafe_account_link_transitions_are_http_conflicts()
    {
        Type publicSupport = typeof(StaffModule).Assembly.GetType(
            "BunkFy.Modules.Staff.Api.StaffApiEndpointSupport",
            throwOnError: true)!;
        ApiErrorStatusCodeMap publicMap = Assert.IsType<ApiErrorStatusCodeMap>(
            publicSupport.GetField(
                "ErrorStatusCodes",
                BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        ApiErrorStatusCodeMap adminMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(StaffAdminApiModule).GetField(
                "ErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        Error[] errors =
        [
            StaffApplicationErrors.AuthSubjectUnlinkRequiresSuspension,
            StaffApplicationErrors.AuthSubjectReplacementRequiresUnlink,
            StaffApplicationErrors.AuthSubjectLinkRequiresActive,
            StaffApplicationErrors.IdentityAnchorResolutionRequired,
            StaffApplicationErrors.IdentityAnchorAccessClosureRequired
        ];

        foreach (Error error in errors)
        {
            Assert.Equal(StatusCodes.Status409Conflict, publicMap.GetStatusCode(error));
            Assert.Equal(StatusCodes.Status409Conflict, adminMap.GetStatusCode(error));
        }
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_create()
    {
        ServiceCollection services = new();
        services.AddSingleton<AdminCliGlobalOptions>();
        using ServiceProvider provider = services.BuildServiceProvider();
        AdminCliGlobalOptions options = provider
            .GetRequiredService<AdminCliGlobalOptions>();
        RootCommand root = new("admin")
        {
            options.ActorOption,
            options.TenantOption,
            options.OutputOption
        };
        AdminCliCommandRegistry registry = new(root, provider);
        new StaffAdminCliModule().MapCommands(registry);
        string[] command =
        [
            "staff", "create", "--display-name", "Maya Chen"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000001"
        ]).Errors);
        Assert.NotEmpty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000001",
            "--auth-subject-id",
            "account-maya"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_update()
    {
        ServiceCollection services = new();
        services.AddSingleton<AdminCliGlobalOptions>();
        using ServiceProvider provider = services.BuildServiceProvider();
        AdminCliGlobalOptions options = provider
            .GetRequiredService<AdminCliGlobalOptions>();
        RootCommand root = new("admin")
        {
            options.ActorOption,
            options.TenantOption,
            options.OutputOption
        };
        AdminCliCommandRegistry registry = new(root, provider);
        new StaffAdminCliModule().MapCommands(registry);
        string[] command =
        [
            "staff", "update",
            "--staff-member-id", "73000000-0000-0000-0000-000000000002",
            "--display-name", "Maya Chen",
            "--expected-version", "4"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000003"
        ]).Errors);
        Assert.NotEmpty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000003",
            "--auth-subject-id",
            "account-maya"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_account_link_changes()
    {
        ServiceCollection services = new();
        services.AddSingleton<AdminCliGlobalOptions>();
        using ServiceProvider provider = services.BuildServiceProvider();
        AdminCliGlobalOptions options = provider
            .GetRequiredService<AdminCliGlobalOptions>();
        RootCommand root = new("admin")
        {
            options.ActorOption,
            options.TenantOption,
            options.OutputOption
        };
        AdminCliCommandRegistry registry = new(root, provider);
        new StaffAdminCliModule().MapCommands(registry);
        string[] command =
        [
            "staff", "set-auth-subject",
            "--staff-member-id", "73000000-0000-0000-0000-000000000004",
            "--auth-subject-id", "account-maya",
            "--expected-version", "4",
            "--yes"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000005"
        ]).Errors);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("resume")]
    public void Admin_cli_requires_operation_identity_for_lifecycle_changes(
        string action)
    {
        using ServiceProvider provider = CreateAdminCliServices();
        RootCommand root = CreateAdminRoot(provider);
        string[] command =
        [
            "staff", action,
            "--staff-member-id", "73000000-0000-0000-0000-000000000006",
            "--reason", "Approved change",
            "--expected-version", "4"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000007"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_departure()
    {
        using ServiceProvider provider = CreateAdminCliServices();
        RootCommand root = CreateAdminRoot(provider);
        string[] command =
        [
            "staff", "depart",
            "--staff-member-id", "73000000-0000-0000-0000-000000000008",
            "--effective-on", "2026-08-07",
            "--reason", "Contract ended",
            "--expected-version", "4",
            "--yes"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id",
            "73000000-0000-0000-0000-000000000009"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_property_assignment_changes()
    {
        using ServiceProvider provider = CreateAdminCliServices();
        RootCommand root = CreateAdminRoot(provider);
        string[] common =
        [
            "--staff-member-id", "73000000-0000-0000-0000-000000000010",
            "--property-id", "73000000-0000-0000-0000-000000000011",
            "--expected-version", "4"
        ];
        string[] assign =
        [
            "staff", "assign-property", .. common,
            "--effective-from", "2026-08-07"
        ];
        string[] unassign =
        [
            "staff", "unassign-property", .. common,
            "--effective-to", "2026-08-07",
            "--reason", "Transferred"
        ];

        Assert.NotEmpty(root.Parse(assign).Errors);
        Assert.NotEmpty(root.Parse(unassign).Errors);
        Assert.Empty(root.Parse([
            .. assign,
            "--operation-id",
            "73000000-0000-0000-0000-000000000012"
        ]).Errors);
        Assert.Empty(root.Parse([
            .. unassign,
            "--operation-id",
            "73000000-0000-0000-0000-000000000013"
        ]).Errors);
    }

    [Fact]
    public async Task Directory_and_sensitive_profile_routes_have_distinct_permissions()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<StaffApiSecurityOptions>(options =>
            options.AccountLinkManagementAssurance =
                new AuthenticationAssuranceRequirement(
                    maxAuthenticationAge: TimeSpan.FromMinutes(10)));
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();
        new StaffModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string member = "/api/staff/members/{staffMemberId:guid}";

        AssertPermissions(endpoints, HttpMethods.Get, member, StaffAdminPermissionCodes.Read);
        AssertPermissions(
            endpoints,
            HttpMethods.Post,
            "/api/staff/members/account-directory/resolve",
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Post,
            "/api/staff/members",
            StaffAdminPermissionCodes.Create);
        AssertPermissions(
            endpoints,
            HttpMethods.Get,
            $"{member}/profile",
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Put,
            member,
            StaffAdminPermissionCodes.Manage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Put,
            $"{member}/auth-subject",
            StaffAdminPermissionCodes.AccountLinksManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertAssurance(
            endpoints,
            HttpMethods.Put,
            $"{member}/auth-subject",
            true);

        AssertResponse<StaffDirectoryListResponse>(endpoints, HttpMethods.Get, "/api/staff/members");
        AssertResponse<StaffAccountDirectoryResponse>(
            endpoints,
            HttpMethods.Post,
            "/api/staff/members/account-directory/resolve");
        AssertResponse<StaffDirectoryMemberDto>(endpoints, HttpMethods.Get, member);
        AssertResponse<StaffMemberDto>(endpoints, HttpMethods.Get, $"{member}/profile");
        AssertResponse<StaffDirectoryMemberDto>(endpoints, HttpMethods.Post, "/api/staff/members");
        AssertResponse<StaffMemberMutationReceiptDto>(endpoints, HttpMethods.Put, member);
        AssertResponse<StaffMemberMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{member}/auth-subject");
        foreach (string action in new[] { "suspend", "resume", "depart" })
        {
            string lifecycle = $"{member}/{action}";
            AssertPermissions(
                endpoints,
                HttpMethods.Post,
                lifecycle,
                StaffAdminPermissionCodes.ManageLifecycle);
            AssertResponse<StaffMemberMutationReceiptDto>(
                endpoints,
                HttpMethods.Post,
                lifecycle);
        }
        AssertResponse<StaffPropertyDirectoryListResponse>(
            endpoints,
            HttpMethods.Get,
            "/api/staff/properties/{propertyId:guid}/members");
        const string propertyMember =
            "/api/staff/properties/{propertyId:guid}/members/{staffMemberId:guid}";
        AssertResponse<StaffMemberMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{propertyMember}/assignment");
        AssertResponse<StaffMemberMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{propertyMember}/unassign");
        const string correction = "/api/staff/data-rights-corrections";
        AssertPermissions(
            endpoints,
            HttpMethods.Post,
            correction,
            DataRightsAdminPermissionCodes.Execute);
        AssertResponse<StaffDataRightsCorrectionReceiptDto>(
            endpoints,
            HttpMethods.Post,
            correction);
    }

    private static ServiceProvider CreateAdminCliServices()
    {
        ServiceCollection services = new();
        services.AddSingleton<AdminCliGlobalOptions>();
        return services.BuildServiceProvider();
    }

    private static RootCommand CreateAdminRoot(ServiceProvider provider)
    {
        AdminCliGlobalOptions options = provider
            .GetRequiredService<AdminCliGlobalOptions>();
        RootCommand root = new("admin")
        {
            options.ActorOption,
            options.TenantOption,
            options.OutputOption
        };
        AdminCliCommandRegistry registry = new(root, provider);
        new StaffAdminCliModule().MapCommands(registry);
        return root;
    }

    [Fact]
    public async Task Governance_and_data_holds_use_dedicated_permissions_and_bounded_assurance()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<StaffApiSecurityOptions>(options =>
        {
            options.EmploymentGovernanceAssurance =
                new AuthenticationAssuranceRequirement(
                    maxAuthenticationAge: TimeSpan.FromMinutes(10));
            options.DataHoldReleaseAssurance =
                new AuthenticationAssuranceRequirement(
                    maxAuthenticationAge: TimeSpan.FromMinutes(10));
        });
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();
        new StaffModule().MapEndpoints(app);

        RouteEndpoint[] endpoints =
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
        ];
        const string member = "/api/staff/{staffMemberId:guid}";
        string governance = $"{member}/employment-governance";
        string holds = $"{member}/data-holds";
        string release = $"{holds}/{{holdId:guid}}/release";

        AssertPermissions(
            endpoints,
            HttpMethods.Get,
            governance,
            StaffAdminPermissionCodes.EmploymentGovernanceManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Put,
            governance,
            StaffAdminPermissionCodes.EmploymentGovernanceManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Get,
            holds,
            StaffAdminPermissionCodes.DataHoldsManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Post,
            holds,
            StaffAdminPermissionCodes.DataHoldsManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);
        AssertPermissions(
            endpoints,
            HttpMethods.Post,
            release,
            StaffAdminPermissionCodes.DataHoldsManage,
            StaffAdminPermissionCodes.SensitiveProfileRead);

        AssertAssurance(endpoints, HttpMethods.Get, governance, false);
        AssertAssurance(endpoints, HttpMethods.Put, governance, true);
        AssertAssurance(endpoints, HttpMethods.Get, holds, false);
        AssertAssurance(endpoints, HttpMethods.Post, holds, false);
        AssertAssurance(endpoints, HttpMethods.Post, release, true);

        AssertResponse<StaffEmploymentGovernanceDto>(
            endpoints,
            HttpMethods.Get,
            governance);
        AssertResponse<StaffEmploymentGovernanceChangeReceiptDto>(
            endpoints,
            HttpMethods.Put,
            governance);
        AssertResponse<StaffDataHoldListResponse>(
            endpoints,
            HttpMethods.Get,
            holds);
        AssertResponse<StaffDataHoldReceiptDto>(
            endpoints,
            HttpMethods.Post,
            holds);
        AssertResponse<StaffDataHoldReceiptDto>(
            endpoints,
            HttpMethods.Post,
            release);
    }

    [Fact]
    public void Sensitive_response_policy_disables_storage()
    {
        Type support = typeof(StaffModule).Assembly.GetType(
            "BunkFy.Modules.Staff.Api.StaffApiEndpointSupport",
            throwOnError: true)!;
        MethodInfo method = support.GetMethod(
            "MarkSensitiveResponse",
            BindingFlags.Public | BindingFlags.Static)!;
        DefaultHttpContext context = new();

        method.Invoke(null, [context]);

        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }

    private static void AssertPermissions(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        params string[] expectedPermissions)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);
        string[] permissions = endpoint.Metadata
            .OfType<AccessPermissionMetadata>()
            .Select(metadata => metadata.Permission.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedPermissions.Order(StringComparer.Ordinal), permissions);
    }

    private static void AssertAssurance(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        bool expected)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata
                .GetMetadata<HttpMethodMetadata>()?
                .HttpMethods.Contains(
                    method,
                    StringComparer.Ordinal) == true);
        bool configured = endpoint.Metadata.Any(metadata =>
            string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal));

        Assert.Equal(expected, configured);
    }

    private static void AssertResponse<TResponse>(
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
}
