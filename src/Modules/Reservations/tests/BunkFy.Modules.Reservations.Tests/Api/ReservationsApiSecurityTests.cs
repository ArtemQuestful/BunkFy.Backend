namespace BunkFy.Modules.Reservations.Tests.Api;

using System.CommandLine;
using System.Reflection;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.AdminApi;
using BunkFy.Modules.Reservations.AdminCli;
using BunkFy.Modules.Reservations.Api;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Administration.Api;
using Gma.Framework.Cqrs;
using Gma.Framework.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationsApiSecurityTests
{
    [Theory]
    [InlineData(typeof(ReservationsModule.CancelReservationRequest))]
    [InlineData(typeof(ReservationsModule.StayLifecycleRequest))]
    [InlineData(typeof(ReservationsModule.UpdateReservationGuestDetailsRequest))]
    [InlineData(typeof(ReservationsAdminApiModule.CancelReservationRequest))]
    [InlineData(typeof(ReservationsAdminApiModule.StayLifecycleRequest))]
    public void Management_requests_require_caller_owned_operation_identity(Type requestType)
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
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_lifecycle_commands()
    {
        ServiceCollection services = new();
        services.AddSingleton<AdminCliGlobalOptions>();
        using ServiceProvider provider = services.BuildServiceProvider();
        AdminCliGlobalOptions options = provider.GetRequiredService<AdminCliGlobalOptions>();
        RootCommand root = new("admin")
        {
            options.ActorOption,
            options.TenantOption,
            options.OutputOption
        };
        AdminCliCommandRegistry registry = new(root, provider);
        new ReservationsAdminCliModule().MapCommands(registry);

        const string propertyId = "71000000-0000-0000-0000-000000000001";
        const string reservationId = "72000000-0000-0000-0000-000000000001";
        const string operationId = "73000000-0000-0000-0000-000000000001";
        string[][] commands =
        [
            [
                "reservations", "cancel", "--property-id", propertyId,
                "--reservation-id", reservationId, "--expected-version", "3",
                "--yes"
            ],
            [
                "reservations", "check-in", "--property-id", propertyId,
                "--reservation-id", reservationId, "--business-date", "2026-10-01",
                "--expected-version", "3", "--yes"
            ],
            [
                "reservations", "no-show", "--property-id", propertyId,
                "--reservation-id", reservationId, "--business-date", "2026-10-01",
                "--expected-version", "3", "--yes"
            ],
            [
                "reservations", "check-out", "--property-id", propertyId,
                "--reservation-id", reservationId, "--business-date", "2026-10-03",
                "--expected-version", "3", "--yes"
            ]
        ];

        foreach (string[] command in commands)
        {
            Assert.NotEmpty(root.Parse(command).Errors);
            Assert.Empty(root.Parse([.. command, "--operation-id", operationId]).Errors);
        }
    }

    [Fact]
    public void Personal_data_response_policies_disable_storage()
    {
        MethodInfo apiPolicy = typeof(ReservationsModule).GetMethod(
            "MarkPersonalDataResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo adminPolicy = typeof(ReservationsAdminApiModule).GetMethod(
            "MarkPersonalDataResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        DefaultHttpContext apiContext = new();
        DefaultHttpContext adminContext = new();

        apiPolicy.Invoke(null, [apiContext]);
        adminPolicy.Invoke(null, [adminContext]);

        AssertNoStore(apiContext);
        AssertNoStore(adminContext);
    }

    [Theory]
    [InlineData(
        "BunkFy.Modules.Reservations.Api.ReservationsNoStoreStartupFilter",
        "/api/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status200OK)]
    [InlineData(
        "BunkFy.Modules.Reservations.Api.ReservationsNoStoreStartupFilter",
        "/api/reservations/properties/not-a-guid/operations-snapshot",
        StatusCodes.Status400BadRequest)]
    [InlineData(
        "BunkFy.Modules.Reservations.Api.ReservationsNoStoreStartupFilter",
        "/api/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status401Unauthorized)]
    [InlineData(
        "BunkFy.Modules.Reservations.Api.ReservationsNoStoreStartupFilter",
        "/api/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status403Forbidden)]
    [InlineData(
        "BunkFy.Modules.Reservations.AdminApi.ReservationsAdminNoStoreStartupFilter",
        "/api/admin/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status200OK)]
    [InlineData(
        "BunkFy.Modules.Reservations.AdminApi.ReservationsAdminNoStoreStartupFilter",
        "/api/admin/reservations/properties/not-a-guid/operations-snapshot",
        StatusCodes.Status400BadRequest)]
    [InlineData(
        "BunkFy.Modules.Reservations.AdminApi.ReservationsAdminNoStoreStartupFilter",
        "/api/admin/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status401Unauthorized)]
    [InlineData(
        "BunkFy.Modules.Reservations.AdminApi.ReservationsAdminNoStoreStartupFilter",
        "/api/admin/reservations/properties/71000000-0000-0000-0000-000000000001/operations-snapshot",
        StatusCodes.Status403Forbidden)]
    public async Task Reservations_path_boundary_disables_storage_for_every_pipeline_outcome(
        string filterTypeName,
        string path,
        int statusCode)
    {
        Type filterType = Assert.Single(
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(filterTypeName, throwOnError: false)),
            type => type is not null)!;
        var filter = Assert.IsType<IStartupFilter>(
            Activator.CreateInstance(filterType, nonPublic: true),
            exactMatch: false);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        filter.Configure(application => application.Run(async context =>
        {
            context.Response.StatusCode = statusCode;
            await context.Response.StartAsync();
        }))(builder);
        RequestDelegate pipeline = builder.Build();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Path = path;

        await pipeline(context);

        Assert.Equal(statusCode, context.Response.StatusCode);
        AssertNoStore(context);
    }

    [Fact]
    public async Task Operational_routes_publish_bounded_response_contracts()
    {
        WebApplicationBuilder builder = CreateApiBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<AdminApiExecutor>(_ => null!);
        await using WebApplication app = builder.Build();

        new ReservationsModule().MapEndpoints(app);
        new ReservationsAdminApiModule().MapEndpoints(app);

        RouteEndpoint[] endpoints =
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
        ];

        const string api = "/api/reservations/properties/{propertyId:guid}";
        AssertResponse<ReservationListResponse>(endpoints, HttpMethods.Get, api);
        RouteEndpoint publicOperations = AssertResponse<ReservationOperationsSnapshotDto>(
            endpoints,
            HttpMethods.Get,
            $"{api}/operations-snapshot");
        AccessPermissionMetadata operationsPermission =
            Assert.Single(publicOperations.Metadata.OfType<AccessPermissionMetadata>());
        Assert.Equal(ReservationsAdminPermissionCodes.Read, operationsPermission.Permission.Value);
        Assert.Equal("reservations-property", operationsPermission.ScopeResolverName);
        AssertResponse<ReservationMutationReceiptDto>(endpoints, HttpMethods.Post, api);
        AssertResponse<ReservationDto>(endpoints, HttpMethods.Get, $"{api}/{{reservationId:guid}}");
        AssertResponse<ReservationDetailsHistoryListResponse>(
            endpoints,
            HttpMethods.Get,
            $"{api}/{{reservationId:guid}}/details-history");
        AssertMutationResponses(endpoints, api, includeGuestDetails: true);

        const string admin = "/api/admin/reservations/properties/{propertyId:guid}";
        AssertResponse<ReservationListResponse>(endpoints, HttpMethods.Get, admin);
        AssertResponse<ReservationOperationsSnapshotDto>(
            endpoints,
            HttpMethods.Get,
            $"{admin}/operations-snapshot");
        AssertResponse<ReservationMutationReceiptDto>(endpoints, HttpMethods.Post, admin);
        AssertResponse<ReservationDto>(endpoints, HttpMethods.Get, $"{admin}/{{reservationId:guid}}");
        AssertResponse<ReservationDetailsHistoryListResponse>(
            endpoints,
            HttpMethods.Get,
            $"{admin}/{{reservationId:guid}}/details-history");
        AssertMutationResponses(endpoints, admin, includeGuestDetails: false);
    }

    [Fact]
    public async Task Data_rights_correction_requires_recent_authentication_at_property_scope()
    {
        WebApplicationBuilder builder = CreateApiBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new ReservationsModule().MapEndpoints(app);

        RouteEndpoint[] endpoints =
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
        ];
        RouteEndpoint correction = Assert.Single(endpoints, endpoint =>
            string.Equals(
                endpoint.RoutePattern.RawText?.Trim('/'),
                "api/reservations/properties/{propertyId:guid}/data-rights-corrections",
                StringComparison.Ordinal) &&
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                HttpMethods.Post,
                StringComparer.Ordinal) == true);
        AccessPermissionMetadata permission =
            Assert.Single(correction.Metadata.OfType<AccessPermissionMetadata>());
        Assert.Equal(DataRightsAdminPermissionCodes.Execute, permission.Permission.Value);
        Assert.Equal("reservations-property", permission.ScopeResolverName);
        AssertAssurance(correction, expected: true);

        RouteEndpoint assured = Assert.Single(endpoints, HasAssurance);
        Assert.Same(correction, assured);
        IProducesResponseTypeMetadata response = Assert.Single(
            correction.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == StatusCodes.Status200OK);
        Assert.Equal(typeof(ReservationDataRightsCorrectionReceiptDto), response.Type);
    }

    [Fact]
    public async Task Data_rights_correction_assurance_is_optional_for_standalone_composition()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOptions<ReservationsApiSecurityOptions>();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new ReservationsModule().MapEndpoints(app);

        RouteEndpoint correction = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>(), endpoint =>
                string.Equals(
                    endpoint.RoutePattern.RawText?.Trim('/'),
                    "api/reservations/properties/{propertyId:guid}/data-rights-corrections",
                    StringComparison.Ordinal) &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                    HttpMethods.Post,
                    StringComparer.Ordinal) == true);

        AssertAssurance(correction, expected: false);
    }

    private static void AssertMutationResponses(
        IEnumerable<RouteEndpoint> endpoints,
        string routeBase,
        bool includeGuestDetails)
    {
        string reservation = $"{routeBase}/{{reservationId:guid}}";
        if (includeGuestDetails)
        {
            AssertResponse<ReservationMutationReceiptDto>(
                endpoints,
                HttpMethods.Put,
                $"{reservation}/guest-details");
        }

        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{reservation}/inventory");
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{reservation}/guests");
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{reservation}/cancel");
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{reservation}/check-in");
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{reservation}/no-show");
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{reservation}/check-out");
    }

    private static WebApplicationBuilder CreateApiBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<ReservationsApiSecurityOptions>(options =>
            options.CorrectionExecutionAssurance = new AuthenticationAssuranceRequirement(
                maxAuthenticationAge: TimeSpan.FromMinutes(10)));
        return builder;
    }

    private static void AssertAssurance(RouteEndpoint endpoint, bool expected) =>
        Assert.Equal(expected, HasAssurance(endpoint));

    private static bool HasAssurance(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(metadata => string.Equals(
            metadata.GetType().Name,
            "AuthenticationAssuranceMetadata",
            StringComparison.Ordinal));

    private static RouteEndpoint AssertResponse<TResponse>(
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
        return endpoint;
    }

    private static void AssertNoStore(HttpContext context)
    {
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }
}
