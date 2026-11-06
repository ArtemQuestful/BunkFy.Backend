namespace BunkFy.Modules.Inventory.Tests.Api;

using System.CommandLine;
using System.Reflection;
using BunkFy.Modules.Inventory.AdminApi;
using BunkFy.Modules.Inventory.AdminCli;
using BunkFy.Modules.Inventory.Api;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Api;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
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
public sealed class InventoryApiSecurityTests
{
    [Theory]
    [InlineData(typeof(InventoryModule.ConfigureSalesModeRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ConfigureSalesModeRequest))]
    public void Sales_mode_requests_require_caller_owned_operation_identity(
        Type requestType)
    {
        PropertyInfo operationId = requestType.GetProperty("OperationId")!;
        ConstructorInfo constructor = Assert.Single(
            requestType.GetConstructors());
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

    [Theory]
    [InlineData(typeof(InventoryModule.RequestBedRetirementRequest))]
    [InlineData(typeof(InventoryModule.RequestRoomRetirementRequest))]
    [InlineData(typeof(InventoryAdminApiModule.RequestBedRetirementRequest))]
    [InlineData(typeof(InventoryAdminApiModule.RequestRoomRetirementRequest))]
    public void Retirement_requests_require_explicit_confirmation(Type requestType)
    {
        PropertyInfo confirmation = requestType.GetProperty("Confirmed")!;
        ConstructorInfo constructor = Assert.Single(requestType.GetConstructors());
        ParameterInfo parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => string.Equals(
                candidate.Name,
                "confirmed",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(bool), confirmation.PropertyType);
        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Theory]
    [InlineData(typeof(InventoryModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReleaseManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReleaseManualBlockGroupRequest))]
    public void Block_group_mutations_require_explicit_confirmation(
        Type requestType)
    {
        PropertyInfo confirmation = requestType.GetProperty("Confirmed")!;
        ConstructorInfo constructor = Assert.Single(requestType.GetConstructors());
        ParameterInfo parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => string.Equals(
                candidate.Name,
                "confirmed",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(bool), confirmation.PropertyType);
        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Theory]
    [InlineData(typeof(InventoryModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReleaseManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReleaseManualBlockGroupRequest))]
    public void Block_group_mutations_require_caller_owned_operation_identity(
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
    }

    [Theory]
    [InlineData(typeof(InventoryModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.CreateManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReplaceManualBlockGroupRequest))]
    public void Block_group_create_or_replace_requires_preview_evidence(
        Type requestType)
    {
        ConstructorInfo constructor = Assert.Single(requestType.GetConstructors());

        foreach ((string name, Type type) in new[]
                 {
                     ("expectedSelectionDigest", typeof(string)),
                     ("expectedAffectedBlockCount", typeof(int))
                 })
        {
            ParameterInfo parameter = Assert.Single(
                constructor.GetParameters(),
                candidate => string.Equals(
                    candidate.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(type, parameter.ParameterType);
            Assert.False(parameter.HasDefaultValue);
        }
    }

    [Theory]
    [InlineData(typeof(InventoryModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryModule.ReleaseManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReplaceManualBlockGroupRequest))]
    [InlineData(typeof(InventoryAdminApiModule.ReleaseManualBlockGroupRequest))]
    public void Block_group_existing_state_mutations_require_expected_version(
        Type requestType)
    {
        PropertyInfo expectedVersion = requestType.GetProperty("ExpectedVersion")!;
        ConstructorInfo constructor = Assert.Single(requestType.GetConstructors());
        ParameterInfo parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => string.Equals(
                candidate.Name,
                "expectedVersion",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(long), expectedVersion.PropertyType);
        Assert.Equal(typeof(long), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void Admin_cli_requires_operation_identity_for_room_configuration()
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
        new InventoryAdminCliModule().MapCommands(registry);
        string[] command =
        [
            "inventory", "rooms", "configure",
            "--property-id", "71000000-0000-0000-0000-000000000001",
            "--room-id", "72000000-0000-0000-0000-000000000001",
            "--sales-mode", "room",
            "--expected-version", "1"
        ];

        Assert.NotEmpty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([
            .. command,
            "--operation-id", "73000000-0000-0000-0000-000000000001"
        ]).Errors);
    }

    [Fact]
    public void Admin_cli_exposes_block_group_preview_mutations_and_recovery()
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
        new InventoryAdminCliModule().MapCommands(registry);
        const string property = "71000000-0000-0000-0000-000000000001";
        const string group = "72000000-0000-0000-0000-000000000001";
        const string operation = "73000000-0000-0000-0000-000000000001";
        string[] targetAndStay =
        [
            "--target-kind", "unit",
            "--unit-id", "74000000-0000-0000-0000-000000000001",
            "--arrival", "2026-08-20",
            "--departure", "2026-08-22",
            "--reason", "Planned maintenance"
        ];

        Assert.Empty(root.Parse([
            "inventory", "block-groups", "preview",
            "--property-id", property,
            .. targetAndStay
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "list",
            "--property-id", property,
            "--cursor", "opaque-cursor",
            "--page-size", "25"
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "members",
            "--property-id", property,
            "--block-group-id", group,
            "--cursor", "opaque-cursor",
            "--page-size", "25"
        ]).Errors);

        string[] confirmedCreate =
        [
            "inventory", "block-groups", "create",
            "--operation-id", operation,
            "--property-id", property,
            .. targetAndStay,
            "--selection-digest", new string('a', 64),
            "--expected-affected-count", "1",
            "--yes"
        ];
        Assert.Empty(root.Parse(confirmedCreate).Errors);
        Assert.NotEmpty(root.Parse([
            "inventory", "block-groups", "create",
            "--operation-id", operation,
            "--property-id", property,
            .. targetAndStay,
            "--expected-affected-count", "1",
            "--yes"
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "replace",
            "--operation-id", operation,
            "--property-id", property,
            "--block-group-id", group,
            "--expected-version", "2",
            .. targetAndStay,
            "--selection-digest", new string('b', 64),
            "--expected-affected-count", "1",
            "--yes"
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "release",
            "--operation-id", operation,
            "--property-id", property,
            "--block-group-id", group,
            "--expected-version", "2",
            "--yes"
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "get-create-operation",
            "--property-id", property,
            "--operation-id", operation
        ]).Errors);
        Assert.Empty(root.Parse([
            "inventory", "block-groups", "get-operation",
            "--property-id", property,
            "--block-group-id", group,
            "--operation-id", operation
        ]).Errors);
    }

    [Fact]
    public void Sensitive_response_policies_disable_storage()
    {
        MethodInfo apiPolicy = typeof(InventoryModule).GetMethod(
            "MarkSensitiveResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo adminPolicy = typeof(InventoryAdminApiModule).GetMethod(
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
    [InlineData(
        typeof(InventoryModule),
        "BunkFy.Modules.Inventory.Api.InventoryNoStoreStartupFilter",
        "/api/inventory/properties/00000000-0000-0000-0000-000000000001/block-groups")]
    [InlineData(
        typeof(InventoryAdminApiModule),
        "BunkFy.Modules.Inventory.AdminApi.InventoryAdminNoStoreStartupFilter",
        "/api/admin/inventory/properties/00000000-0000-0000-0000-000000000001/block-groups")]
    public async Task Inventory_path_boundary_disables_storage_for_every_pipeline_outcome(
        Type moduleType,
        string startupFilterTypeName,
        string path)
    {
        Type startupFilterType = Assert.IsType<Type>(
            moduleType.Assembly.GetType(startupFilterTypeName),
            exactMatch: false);
        IStartupFilter startupFilter = Assert.IsType<IStartupFilter>(
            Activator.CreateInstance(startupFilterType, nonPublic: true),
            exactMatch: false);
        int[] statusCodes =
        [
            StatusCodes.Status200OK,
            StatusCodes.Status400BadRequest,
            StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden,
            StatusCodes.Status409Conflict,
            StatusCodes.Status422UnprocessableEntity,
        ];

        foreach (int statusCode in statusCodes)
        {
            await using ServiceProvider provider = new ServiceCollection()
                .BuildServiceProvider();
            ApplicationBuilder application = new(provider);
            startupFilter.Configure(next => next.Run(async context =>
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsync("{}");
            }))(application);
            DefaultHttpContext context = new()
            {
                RequestServices = provider,
            };
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();

            await application.Build()(context);
            await context.Response.StartAsync();

            AssertNoStore(context);
        }
    }

    [Fact]
    public async Task Operational_routes_publish_explicit_response_contracts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOptions<InventoryApiSecurityOptions>();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<AdminApiExecutor>(_ => null!);
        await using WebApplication app = builder.Build();

        new InventoryModule().MapEndpoints(app);
        new InventoryAdminApiModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];

        AssertOperationalResponses(endpoints, "/api/inventory/properties/{propertyId:guid}");
        AssertOperationalResponses(endpoints, "/api/admin/inventory/properties/{propertyId:guid}");
    }

    [Fact]
    public async Task Configured_assurance_and_retirement_permission_protect_only_new_intent()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<InventoryApiSecurityOptions>(options =>
            options.TopologyRetirementAssurance = new(
                maxAuthenticationAge: TimeSpan.FromMinutes(10)));
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new InventoryModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string property = "/api/inventory/properties/{propertyId:guid}";
        const string room = $"{property}/rooms/{{roomId:guid}}";
        RouteEndpoint bedRequest = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{room}/beds/{{bedId:guid}}/retirement");
        RouteEndpoint roomRequest = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{room}/retirement");
        RouteEndpoint bedStatus = FindEndpoint(
            endpoints,
            HttpMethods.Get,
            $"{property}/bed-retirements/{{topologyChangeId:guid}}");
        RouteEndpoint bedRetry = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{property}/bed-retirements/{{topologyChangeId:guid}}/retry");
        RouteEndpoint bedCancel = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{property}/bed-retirements/{{topologyChangeId:guid}}/cancel");
        RouteEndpoint roomStatus = FindEndpoint(
            endpoints,
            HttpMethods.Get,
            $"{property}/room-retirements/{{topologyChangeId:guid}}");
        RouteEndpoint roomRetry = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{property}/room-retirements/{{topologyChangeId:guid}}/retry");
        RouteEndpoint roomCancel = FindEndpoint(
            endpoints,
            HttpMethods.Post,
            $"{property}/room-retirements/{{topologyChangeId:guid}}/cancel");

        AssertAssurance(bedRequest, expected: true);
        AssertAssurance(roomRequest, expected: true);
        AssertPermission(bedRequest, InventoryAdminPermissionCodes.Retire);
        AssertPermission(roomRequest, InventoryAdminPermissionCodes.Retire);
        AssertAssurance(bedStatus, expected: false);
        AssertAssurance(bedRetry, expected: false);
        AssertAssurance(bedCancel, expected: false);
        AssertAssurance(roomStatus, expected: false);
        AssertAssurance(roomRetry, expected: false);
        AssertAssurance(roomCancel, expected: false);
        AssertPermission(bedStatus, InventoryAdminPermissionCodes.Retire);
        AssertPermission(bedRetry, InventoryAdminPermissionCodes.Retire);
        AssertPermission(bedCancel, InventoryAdminPermissionCodes.Retire);
        AssertPermission(roomStatus, InventoryAdminPermissionCodes.Retire);
        AssertPermission(roomRetry, InventoryAdminPermissionCodes.Retire);
        AssertPermission(roomCancel, InventoryAdminPermissionCodes.Retire);
        foreach (string path in new[] { $"{room}/retirement", $"{room}/beds/{{bedId:guid}}/retirement" })
        {
            RouteEndpoint context = FindEndpoint(endpoints, HttpMethods.Get, path);
            AssertPermission(context, InventoryAdminPermissionCodes.Retire);
            AssertAssurance(context, expected: false);
            AssertResponse<InventoryRetirementContextDto>(endpoints, HttpMethods.Get, path);
        }
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Put, $"{room}/sales-mode"),
            expected: false);
        AssertAssurance(
            FindEndpoint(endpoints, HttpMethods.Post, $"{property}/blocks"),
            expected: false);
    }

    [Fact]
    public async Task Public_block_group_routes_split_read_and_management_permissions()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOptions<InventoryApiSecurityOptions>();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new InventoryModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string property = "/api/inventory/properties/{propertyId:guid}";
        string groups = $"{property}/block-groups";

        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Get, groups),
            InventoryAdminPermissionCodes.Read);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Get, $"{groups}/{{blockGroupId:guid}}"),
            InventoryAdminPermissionCodes.Read);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Get, $"{groups}/{{blockGroupId:guid}}/members"),
            InventoryAdminPermissionCodes.Read);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Post, $"{groups}/preview"),
            InventoryAdminPermissionCodes.BlockGroupsManage);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Post, groups),
            InventoryAdminPermissionCodes.BlockGroupsManage);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Put, $"{groups}/{{blockGroupId:guid}}"),
            InventoryAdminPermissionCodes.BlockGroupsManage);
        AssertPermission(
            FindEndpoint(endpoints, HttpMethods.Post, $"{groups}/{{blockGroupId:guid}}/release"),
            InventoryAdminPermissionCodes.BlockGroupsManage);
        AssertPermission(
            FindEndpoint(
                endpoints,
                HttpMethods.Get,
                $"{property}/block-group-create-operations/{{operationId:guid}}"),
            InventoryAdminPermissionCodes.BlockGroupsManage);
        AssertPermission(
            FindEndpoint(
                endpoints,
                HttpMethods.Get,
                $"{groups}/{{blockGroupId:guid}}/operations/{{operationId:guid}}"),
            InventoryAdminPermissionCodes.BlockGroupsManage);
    }

    [Fact]
    public void Block_group_errors_have_stable_public_and_admin_statuses()
    {
        ApiErrorStatusCodeMap publicMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(InventoryModule).GetField(
                "PublicErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        ApiErrorStatusCodeMap adminMap = Assert.IsType<ApiErrorStatusCodeMap>(
            typeof(InventoryAdminApiModule).GetField(
                "AdminErrorStatusCodes",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        (Error Error, int Status)[] mappings =
        [
            (InventoryApplicationErrors.BlockGroupConfirmationRequired, StatusCodes.Status400BadRequest),
            (InventoryApplicationErrors.BlockGroupSelectionDigestInvalid, StatusCodes.Status400BadRequest),
            (InventoryApplicationErrors.BlockGroupCursorInvalid, StatusCodes.Status400BadRequest),
            (InventoryApplicationErrors.BlockGroupNotFound, StatusCodes.Status404NotFound),
            (InventoryApplicationErrors.BlockGroupOperationNotFound, StatusCodes.Status404NotFound),
            (InventoryApplicationErrors.BlockGroupSelectionMismatch, StatusCodes.Status409Conflict),
            (InventoryApplicationErrors.VersionConflict, StatusCodes.Status409Conflict),
            (InventoryApplicationErrors.ManagementOperationConflict, StatusCodes.Status409Conflict),
            (InventoryApplicationErrors.BlockGroupTargetTooLarge, StatusCodes.Status422UnprocessableEntity),
            (Domain.Errors.InventoryDomainErrors.BlockGroupActorInvalid, StatusCodes.Status400BadRequest),
            (InventoryApplicationErrors.WorkspaceProcessingRestricted, StatusCodes.Status423Locked),
            (InventoryApplicationErrors.WorkspaceProcessingAdmissionUnavailable, StatusCodes.Status503ServiceUnavailable)
        ];

        foreach ((Error error, int status) in mappings)
        {
            Assert.Equal(status, publicMap.GetStatusCode(error));
            Assert.Equal(status, adminMap.GetStatusCode(error));
        }

        Assert.Equal(
            StatusCodes.Status409Conflict,
            publicMap.GetStatusCode(InventoryApplicationErrors.BlockTargetEmpty));
        Assert.Equal(
            StatusCodes.Status409Conflict,
            adminMap.GetStatusCode(InventoryApplicationErrors.BlockOverlap));
        Assert.Equal(
            StatusCodes.Status409Conflict,
            publicMap.GetStatusCode(InventoryApplicationErrors.BlockAllocationConflict));
    }

    private static void AssertOperationalResponses(
        IEnumerable<RouteEndpoint> endpoints,
        string routeBase)
    {
        string rooms = $"{routeBase}/rooms";
        AssertResponse<RoomInventoryListResponse>(endpoints, HttpMethods.Get, rooms);
        AssertResponse<RoomInventoryMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{rooms}/{{roomId:guid}}/sales-mode");
        AssertResponse<RoomInventoryChangeImpactDto>(
            endpoints,
            HttpMethods.Get,
            $"{rooms}/{{roomId:guid}}/change-impact");
        AssertResponse<InventoryAvailabilityResponse>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/availability");

        string blocks = $"{routeBase}/blocks";
        AssertResponse<ManualInventoryBlockListResponse>(endpoints, HttpMethods.Get, blocks);
        AssertResponse<ManualInventoryBlockMutationReceiptDto>(endpoints, HttpMethods.Post, blocks);
        AssertResponse<ManualInventoryBlockMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{blocks}/{{blockId:guid}}/release");
        AssertResponse<ManualInventoryBlockGroupMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/block-groups");
        AssertResponse<ManualInventoryBlockGroupSelectionPreviewDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/block-groups/preview");
        AssertResponse<ManualInventoryBlockGroupListResponse>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/block-groups");
        AssertResponse<ManualInventoryBlockGroupDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/block-groups/{{blockGroupId:guid}}");
        AssertResponse<ManualInventoryBlockGroupMemberListResponse>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/block-groups/{{blockGroupId:guid}}/members");
        AssertResponse<ManualInventoryBlockGroupMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            $"{routeBase}/block-groups/{{blockGroupId:guid}}");
        AssertResponse<ManualInventoryBlockGroupMutationReceiptDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/block-groups/{{blockGroupId:guid}}/release");
        AssertResponse<ManualInventoryBlockGroupOperationDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/block-group-create-operations/{{operationId:guid}}");
        AssertResponse<ManualInventoryBlockGroupOperationDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/block-groups/{{blockGroupId:guid}}/operations/{{operationId:guid}}");

        AssertResponse<BedRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{rooms}/{{roomId:guid}}/beds/{{bedId:guid}}/retirement");
        AssertResponse<BedRetirementDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/bed-retirements/{{topologyChangeId:guid}}");
        AssertResponse<BedRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/bed-retirements/{{topologyChangeId:guid}}/retry");
        AssertResponse<BedRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/bed-retirements/{{topologyChangeId:guid}}/cancel");
        AssertResponse<RoomRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{rooms}/{{roomId:guid}}/retirement");
        AssertResponse<RoomRetirementDto>(
            endpoints,
            HttpMethods.Get,
            $"{routeBase}/room-retirements/{{topologyChangeId:guid}}");
        AssertResponse<RoomRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/room-retirements/{{topologyChangeId:guid}}/retry");
        AssertResponse<RoomRetirementDto>(
            endpoints,
            HttpMethods.Post,
            $"{routeBase}/room-retirements/{{topologyChangeId:guid}}/cancel");
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

    private static void AssertAssurance(RouteEndpoint endpoint, bool expected)
    {
        bool configured = endpoint.Metadata.Any(metadata =>
            string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal));

        Assert.Equal(expected, configured);
    }

    private static void AssertPermission(RouteEndpoint endpoint, string expected) =>
        Assert.Equal(
            expected,
            Assert.Single(endpoint.Metadata.OfType<AccessPermissionMetadata>())
                .Permission.Value);

    private static void AssertNoStore(HttpContext context)
    {
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }
}
