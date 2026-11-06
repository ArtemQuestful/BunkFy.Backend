namespace BunkFy.Modules.Properties.Tests.Api;

using System.CommandLine;
using System.Reflection;
using BunkFy.Modules.Properties.AdminApi;
using BunkFy.Modules.Properties.AdminCli;
using BunkFy.Modules.Properties.Api;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
using Gma.Framework.Administration;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Api;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Api.Results;
using Gma.Framework.Results;
using Gma.Framework.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesApiSecurityTests
{
    [Fact]
    public void Topology_write_contracts_require_operation_ids()
    {
        Type[] requestTypes =
        [
            typeof(PropertiesModule.RoomCreateRequest),
            typeof(PropertiesModule.RoomUpdateRequest),
            typeof(PropertiesModule.BedWriteRequest),
            typeof(PropertiesModule.BedBatchWriteRequest),
            typeof(PropertiesAdminApiModule.RoomCreateRequest),
            typeof(PropertiesAdminApiModule.RoomUpdateRequest),
            typeof(PropertiesAdminApiModule.BedWriteRequest),
            typeof(PropertiesAdminApiModule.BedBatchWriteRequest)
        ];

        Assert.All(
            requestTypes,
            requestType => Assert.Equal(
                typeof(Guid),
                requestType.GetProperty("OperationId")?.PropertyType));
    }

    [Fact]
    public void Time_zone_set_requests_expose_confirmation_but_never_actor_provenance()
    {
        Type[] requestTypes =
        [
            typeof(PropertiesModule.SetPropertyTimeZoneRequest),
            typeof(PropertiesAdminApiModule.SetPropertyTimeZoneRequest)
        ];

        Assert.All(requestTypes, requestType =>
        {
            Assert.Equal(typeof(Guid), requestType.GetProperty("OperationId")?.PropertyType);
            Assert.Equal(typeof(bool), requestType.GetProperty("Confirmed")?.PropertyType);
            Assert.Null(requestType.GetProperty("ActorId"));
        });
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_bed_mutations()
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
        new PropertiesAdminCliModule().MapCommands(registry);

        const string propertyId =
            "71000000-0000-0000-0000-000000000001";
        const string roomId =
            "72000000-0000-0000-0000-000000000001";
        const string bedId =
            "73000000-0000-0000-0000-000000000001";
        const string operationId =
            "74000000-0000-0000-0000-000000000001";
        string[][] commands =
        [
            [
                "properties", "beds", "add",
                "--property-id", propertyId,
                "--room-id", roomId,
                "--expected-room-version", "3",
                "--label", "A"
            ],
            [
                "properties", "beds", "add-many",
                "--property-id", propertyId,
                "--room-id", roomId,
                "--expected-room-version", "3",
                "--label", "A", "B"
            ],
            [
                "properties", "beds", "update",
                "--property-id", propertyId,
                "--room-id", roomId,
                "--bed-id", bedId,
                "--expected-room-version", "3",
                "--label", "A"
            ]
        ];

        foreach (string[] command in commands)
        {
            Assert.NotEmpty(root.Parse(command).Errors);
            Assert.Empty(root.Parse([
                .. command,
                "--operation-id",
                operationId
            ]).Errors);
        }
    }

    [Fact]
    public void Admin_cli_requires_create_time_zone_and_allows_update_omission()
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
        new PropertiesAdminCliModule().MapCommands(registry);

        const string propertyId = "71000000-0000-0000-0000-000000000001";
        const string operationId = "74000000-0000-0000-0000-000000000001";
        string[] create =
        [
            "properties", "create",
            "--operation-id", operationId,
            "--name", "Canal House",
            "--code", "AMS"
        ];
        string[] update =
        [
            "properties", "update",
            "--property-id", propertyId,
            "--operation-id", operationId,
            "--name", "Canal House",
            "--code", "AMS",
            "--expected-version", "3"
        ];

        Assert.NotEmpty(root.Parse(create).Errors);
        Assert.Empty(root.Parse([
            .. create,
            "--time-zone", "Europe/Amsterdam"
        ]).Errors);
        Assert.Empty(root.Parse(update).Errors);
        Assert.Empty(root.Parse([
            .. update,
            "--time-zone", "Europe/Amsterdam"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_time_zone_set_accepts_optional_confirmation_and_requires_recovery_identity()
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
        new PropertiesAdminCliModule().MapCommands(registry);

        const string propertyId = "71000000-0000-0000-0000-000000000001";
        const string operationId = "74000000-0000-0000-0000-000000000001";
        string[] set =
        [
            "properties", "time-zones", "set",
            "--property-id", propertyId,
            "--operation-id", operationId,
            "--time-zone", "Europe/Amsterdam",
            "--expected-version", "3"
        ];

        Assert.Empty(root.Parse(set).Errors);
        Assert.Empty(root.Parse([.. set, "--yes"]).Errors);
        Assert.Empty(root.Parse([
            "properties", "time-zones", "operation-get",
            "--property-id", propertyId,
            "--operation-id", operationId
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_time_zone_catalog_uses_precise_country_code_validation()
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
        new PropertiesAdminCliModule().MapCommands(registry);

        Assert.NotEmpty(root.Parse([
            "properties", "time-zones", "catalog", "--country", "NL"
        ]).Errors);
        Assert.NotEmpty(root.Parse([
            "properties", "time-zones", "catalog", "--country-code", "N1"
        ]).Errors);
        Assert.NotEmpty(root.Parse([
            "properties", "time-zones", "catalog", "--country-code", "NLD"
        ]).Errors);
        Assert.Empty(root.Parse([
            "properties", "time-zones", "catalog", "--country-code", "nl"
        ]).Errors);
        Assert.Empty(root.Parse([
            "properties", "time-zones", "catalog",
            "--property-id", "71000000-0000-0000-0000-000000000001",
            "--country-code", "nl"
        ]).Errors);

        MethodInfo normalizer = typeof(PropertiesAdminCliModule).GetMethod(
            "NormalizeCountryCode",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("NL", normalizer.Invoke(null, [" nl "]));
    }

    [Fact]
    public void Admin_time_zone_provenance_uses_the_authenticated_actor_context_and_fails_closed()
    {
        MethodInfo apiResolver = typeof(PropertiesAdminApiModule).GetMethod(
            "ResolveAdminActor",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo cliResolver = typeof(PropertiesAdminCliModule).GetMethod(
            "ResolveAdminActor",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var populated = new StubAdminActorContext(AdminActor.System("operator-17"));
        var empty = new StubAdminActorContext(null);

        Result<string> apiActor = Assert.IsType<Result<string>>(
            apiResolver.Invoke(null, [populated]));
        Result<string> missingApiActor = Assert.IsType<Result<string>>(
            apiResolver.Invoke(null, [empty]));
        Assert.True(apiActor.IsSuccess);
        Assert.Equal("admin-api:operator-17", apiActor.Value);
        Assert.True(missingApiActor.IsFailure);
        Assert.Equal(AdminErrors.Unauthorized, missingApiActor.Error);

        Result<string> cliActor = Assert.IsType<Result<string>>(
            cliResolver.Invoke(null, [populated]));
        Result<string> missingCliActor = Assert.IsType<Result<string>>(
            cliResolver.Invoke(null, [empty]));
        Assert.True(cliActor.IsSuccess);
        Assert.Equal("admin-cli:operator-17", cliActor.Value);
        Assert.True(missingCliActor.IsFailure);
        Assert.Equal(AdminErrors.Unauthorized, missingCliActor.Error);

        var maximum = new StubAdminActorContext(AdminActor.System(new string('a', 190)));
        var overlength = new StubAdminActorContext(AdminActor.System(new string('a', 191)));
        Result<string> maximumApiActor = Assert.IsType<Result<string>>(
            apiResolver.Invoke(null, [maximum]));
        Result<string> maximumCliActor = Assert.IsType<Result<string>>(
            cliResolver.Invoke(null, [maximum]));
        Result<string> overlengthApiActor = Assert.IsType<Result<string>>(
            apiResolver.Invoke(null, [overlength]));
        Result<string> overlengthCliActor = Assert.IsType<Result<string>>(
            cliResolver.Invoke(null, [overlength]));
        Assert.True(maximumApiActor.IsSuccess);
        Assert.True(maximumCliActor.IsSuccess);
        Assert.True(overlengthApiActor.IsFailure);
        Assert.True(overlengthCliActor.IsFailure);
        Assert.Equal(PropertiesContractLimits.ActorIdMaxLength, maximumApiActor.Value.Length);
        Assert.Equal(PropertiesContractLimits.ActorIdMaxLength, maximumCliActor.Value.Length);
        Assert.Equal(PropertiesDomainErrors.ActorIdInvalid, overlengthApiActor.Error);
        Assert.Equal(PropertiesDomainErrors.ActorIdInvalid, overlengthCliActor.Error);
    }

    [Fact]
    public void Sensitive_response_policies_disable_storage()
    {
        MethodInfo apiPolicy = typeof(PropertiesModule).GetMethod(
            "MarkSensitiveResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo adminPolicy = typeof(PropertiesAdminApiModule).GetMethod(
            "MarkSensitiveResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        DefaultHttpContext apiContext = new();
        DefaultHttpContext adminContext = new();

        apiPolicy.Invoke(null, [apiContext]);
        adminPolicy.Invoke(null, [adminContext]);

        AssertNoStore(apiContext);
        AssertNoStore(adminContext);
    }

    [Theory]
    [InlineData("TimeZoneQueryInvalid", StatusCodes.Status400BadRequest)]
    [InlineData("TimeZoneOperationNotFound", StatusCodes.Status404NotFound)]
    [InlineData("TimeZoneDedicatedOperationRequired", StatusCodes.Status409Conflict)]
    [InlineData("TimeSourceUnavailable", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("TimeZoneRuntimeUnavailable", StatusCodes.Status503ServiceUnavailable)]
    public void Time_zone_failures_have_matching_public_and_admin_http_semantics(
        string errorName,
        int expectedStatusCode)
    {
        Error error = Assert.IsType<Error>(typeof(BunkFy.Modules.Properties.Application.PropertiesApplicationErrors)
            .GetField(errorName, BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null));
        ApiErrorStatusCodeMap publicMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesModule).GetField(
                "PublicErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        ApiErrorStatusCodeMap adminMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesAdminApiModule).GetField(
                "AdminErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));

        Assert.Equal(expectedStatusCode, publicMap.GetStatusCode(error));
        Assert.Equal(expectedStatusCode, adminMap.GetStatusCode(error));
    }

    [Fact]
    public void Invalid_server_derived_actor_has_matching_public_and_admin_http_semantics()
    {
        Error error = BunkFy.Modules.Properties.Domain.Errors.PropertiesDomainErrors.ActorIdInvalid;
        ApiErrorStatusCodeMap publicMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesModule).GetField(
                "PublicErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        ApiErrorStatusCodeMap adminMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesAdminApiModule).GetField(
                "AdminErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));

        Assert.Equal(StatusCodes.Status400BadRequest, publicMap.GetStatusCode(error));
        Assert.Equal(StatusCodes.Status400BadRequest, adminMap.GetStatusCode(error));
    }

    [Theory]
    [InlineData(nameof(PropertiesDomainErrors.TimeZoneRequired))]
    [InlineData(nameof(PropertiesDomainErrors.TimeZoneTooLong))]
    [InlineData(nameof(PropertiesDomainErrors.TimeZoneInvalid))]
    public void Invalid_time_zone_inputs_have_matching_public_and_admin_http_semantics(
        string errorName)
    {
        Error error = Assert.IsType<Error>(typeof(PropertiesDomainErrors)
            .GetField(errorName, BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null));
        ApiErrorStatusCodeMap publicMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesModule).GetField(
                "PublicErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        ApiErrorStatusCodeMap adminMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(PropertiesAdminApiModule).GetField(
                "AdminErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));

        Assert.Equal(StatusCodes.Status400BadRequest, publicMap.GetStatusCode(error));
        Assert.Equal(StatusCodes.Status400BadRequest, adminMap.GetStatusCode(error));
    }

    [Theory]
    [InlineData(
        "BunkFy.Modules.Properties.Api.PropertiesNoStoreStartupFilter",
        "/api/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status200OK)]
    [InlineData(
        "BunkFy.Modules.Properties.Api.PropertiesNoStoreStartupFilter",
        "/api/properties/not-a-guid/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status400BadRequest)]
    [InlineData(
        "BunkFy.Modules.Properties.Api.PropertiesNoStoreStartupFilter",
        "/api/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status401Unauthorized)]
    [InlineData(
        "BunkFy.Modules.Properties.Api.PropertiesNoStoreStartupFilter",
        "/api/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status403Forbidden)]
    [InlineData(
        "BunkFy.Modules.Properties.Api.PropertiesNoStoreStartupFilter",
        "/api/properties/71000000-0000-0000-0000-000000000001/time-zones/catalog",
        StatusCodes.Status200OK)]
    [InlineData(
        "BunkFy.Modules.Properties.AdminApi.PropertiesAdminNoStoreStartupFilter",
        "/api/admin/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status200OK)]
    [InlineData(
        "BunkFy.Modules.Properties.AdminApi.PropertiesAdminNoStoreStartupFilter",
        "/api/admin/properties/not-a-guid/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status400BadRequest)]
    [InlineData(
        "BunkFy.Modules.Properties.AdminApi.PropertiesAdminNoStoreStartupFilter",
        "/api/admin/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status401Unauthorized)]
    [InlineData(
        "BunkFy.Modules.Properties.AdminApi.PropertiesAdminNoStoreStartupFilter",
        "/api/admin/properties/71000000-0000-0000-0000-000000000001/time-zone/operations/74000000-0000-0000-0000-000000000001",
        StatusCodes.Status403Forbidden)]
    [InlineData(
        "BunkFy.Modules.Properties.AdminApi.PropertiesAdminNoStoreStartupFilter",
        "/api/admin/properties/71000000-0000-0000-0000-000000000001/time-zones/catalog",
        StatusCodes.Status200OK)]
    public async Task Properties_path_boundary_disables_storage_for_every_pipeline_outcome(
        string filterTypeName,
        string path,
        int statusCode)
    {
        Assembly assembly = filterTypeName.Contains(".AdminApi.", StringComparison.Ordinal)
            ? typeof(PropertiesAdminApiModule).Assembly
            : typeof(PropertiesModule).Assembly;
        Type filterType = assembly.GetType(filterTypeName, throwOnError: true)!;
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
    public async Task Operational_routes_publish_explicit_response_contracts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOptions<PropertiesApiSecurityOptions>();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<AdminApiExecutor>(_ => null!);
        await using WebApplication app = builder.Build();

        new PropertiesModule().MapEndpoints(app);
        new PropertiesAdminApiModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertTopologyResponses(endpoints, "/api/properties");
        AssertTopologyResponses(endpoints, "/api/admin/properties");
        AssertTimeZoneResponses(endpoints, "/api/properties");
        AssertTimeZoneResponses(endpoints, "/api/admin/properties");
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Get, "/api/properties/time-zones/catalog"),
            PropertiesAdminPermissionCodes.Read,
            "tenant");
        AssertPermission(
            FindEndpoint(
                endpoints,
                HttpMethods.Get,
                "/api/properties/{propertyId:guid}/time-zones/catalog"),
            PropertiesAdminPermissionCodes.Read,
            "properties-property");
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Get, "/api/properties/time-zones/compliance"),
            PropertiesAdminPermissionCodes.TimeZonesManage,
            "tenant");
        AssertPermission(
            FindEndpoint(
                endpoints,
                HttpMethods.Put,
                "/api/properties/{propertyId:guid}/time-zone"),
            PropertiesAdminPermissionCodes.TimeZonesManage,
            "properties-property");
        AssertPermission(
            FindEndpoint(
                endpoints,
                HttpMethods.Get,
                "/api/properties/{propertyId:guid}/time-zone/operations/{operationId:guid}"),
            PropertiesAdminPermissionCodes.TimeZonesManage,
            "properties-property");
        AssertResponse<CountryPolicyListResponse>(
            endpoints,
            HttpMethods.Get,
            "/api/properties/{propertyId:guid}/country-policies");
        AssertResponse<PropertyProcessingStateDto>(
            endpoints,
            HttpMethods.Get,
            "/api/properties/{propertyId:guid}/processing");
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            "/api/properties/{propertyId:guid}/processing",
            StatusCodes.Status503ServiceUnavailable);
        AssertResponse<PropertyMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            "/api/properties/{propertyId:guid}/processing/activate");
        AssertStatus(
            endpoints,
            HttpMethods.Post,
            "/api/properties/{propertyId:guid}/processing/activate",
            StatusCodes.Status503ServiceUnavailable);
        AssertResponse<PropertyMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            "/api/properties/{propertyId:guid}/processing/suspend");
    }

    [Fact]
    public async Task Configured_assurance_protects_only_sensitive_property_controls()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        AuthenticationAssuranceRequirement assurance = new(
            maxAuthenticationAge: TimeSpan.FromMinutes(10));
        builder.Services.Configure<PropertiesApiSecurityOptions>(options =>
        {
            options.ProcessingActivationAssurance = assurance;
            options.PropertyRetirementAssurance = assurance;
            options.TimeZoneManagementAssurance = assurance;
        });
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new PropertiesModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string property = "/api/properties/{propertyId:guid}";

        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Post, $"{property}/retire"),
            expected: true);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Post, $"{property}/processing/activate"),
            expected: true);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Put, $"{property}/time-zone"),
            expected: true);
        AssertAssurance(
            FindEndpoint(
                endpoints,
                HttpMethods.Get,
                $"{property}/time-zone/operations/{{operationId:guid}}"),
            expected: false);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Post, $"{property}/processing/suspend"),
            expected: false);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Put, property),
            expected: false);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Post, $"{property}/rooms"),
            expected: false);
    }

    private static void AssertTopologyResponses(IEnumerable<RouteEndpoint> endpoints, string routeBase)
    {
        AssertResponse<PropertyListResponse>(endpoints, HttpMethods.Get, routeBase);
        AssertResponse<PropertyDto>(endpoints, HttpMethods.Get, $"{routeBase}/{{propertyId:guid}}");
        AssertResponse<PropertyMutationReceiptDto>(endpoints, HttpMethods.Post, routeBase);
        AssertResponse<PropertyMutationReceiptDto>(endpoints, HttpMethods.Put, $"{routeBase}/{{propertyId:guid}}");
        AssertResponse<PropertyMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/{{propertyId:guid}}/retire");
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            routeBase,
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/{{propertyId:guid}}",
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Post,
            routeBase,
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Put,
            $"{routeBase}/{{propertyId:guid}}",
            StatusCodes.Status503ServiceUnavailable);

        string rooms = $"{routeBase}/{{propertyId:guid}}/rooms";
        AssertResponse<RoomListResponse>(endpoints, HttpMethods.Get, rooms);
        AssertResponse<RoomMutationReceiptDto>(endpoints, HttpMethods.Post, rooms);
        AssertResponse<RoomDto>(endpoints, HttpMethods.Get, $"{rooms}/{{roomId:guid}}");
        AssertResponse<RoomMutationReceiptDto>(endpoints, HttpMethods.Put, $"{rooms}/{{roomId:guid}}");
        AssertStatus(
            endpoints,
            HttpMethods.Post,
            $"{rooms}/{{roomId:guid}}/retire",
            StatusCodes.Status204NoContent);

        string beds = $"{rooms}/{{roomId:guid}}/beds";
        AssertResponse<BedListResponse>(endpoints, HttpMethods.Get, beds);
        AssertResponse<BedMutationReceiptDto>(endpoints, HttpMethods.Post, beds);
        AssertResponse<BedBatchMutationReceiptDto>(endpoints, HttpMethods.Post, $"{beds}/batch");
        AssertResponse<BedMutationReceiptDto>(endpoints, HttpMethods.Put, $"{beds}/{{bedId:guid}}");
        AssertStatus(
            endpoints,
            HttpMethods.Post,
            $"{beds}/{{bedId:guid}}/retire",
            StatusCodes.Status204NoContent);
    }

    private static void AssertTimeZoneResponses(
        IEnumerable<RouteEndpoint> endpoints,
        string routeBase)
    {
        AssertResponse<PropertyTimeZoneCatalogPageDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/time-zones/catalog");
        AssertResponse<PropertyTimeZoneCatalogPageDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/{{propertyId:guid}}/time-zones/catalog");
        AssertResponse<PropertyTimeZoneCompliancePageDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/time-zones/compliance");
        AssertResponse<SetPropertyTimeZoneReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{routeBase}/{{propertyId:guid}}/time-zone");
        AssertResponse<PropertyTimeZoneRecoveryDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/{{propertyId:guid}}/time-zone/operations/{{operationId:guid}}");
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/time-zones/catalog",
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/{{propertyId:guid}}/time-zones/catalog",
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/time-zones/compliance",
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Put,
            $"{routeBase}/{{propertyId:guid}}/time-zone",
            StatusCodes.Status503ServiceUnavailable);
        AssertStatus(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/{{propertyId:guid}}/time-zone/operations/{{operationId:guid}}",
            StatusCodes.Status503ServiceUnavailable);
    }

    private static void AssertResponse<TResponse>(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        IProducesResponseTypeMetadata response = Assert.Single(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == StatusCodes.Status200OK);

        Assert.Equal(typeof(TResponse), response.Type);
    }

    private static void AssertStatus(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        int statusCode)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        Assert.Contains(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == statusCode);
    }

    private static RouteEndpoint FindEndpoint(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route) =>
        Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);

    private static void AssertNoStore(HttpContext context)
    {
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }

    private static void AssertAssurance(RouteEndpoint endpoint, bool expected)
    {
        bool configured = endpoint.Metadata.Any(metadata =>
            string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal));

        Assert.Equal(expected, configured);
    }

    private static void AssertPermission(
        RouteEndpoint endpoint,
        string permissionCode,
        string scopeResolverName)
    {
        AccessPermissionMetadata permission = Assert.Single(
            endpoint.Metadata.OfType<AccessPermissionMetadata>());
        Assert.Equal(permissionCode, permission.Permission.Value);
        Assert.Equal(scopeResolverName, permission.ScopeResolverName);
    }

    private sealed class StubAdminActorContext(AdminActor? actor) : IAdminActorContext
    {
        public AdminActor? Actor { get; } = actor;
    }
}
