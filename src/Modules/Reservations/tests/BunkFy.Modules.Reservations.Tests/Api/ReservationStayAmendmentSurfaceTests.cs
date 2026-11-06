namespace BunkFy.Modules.Reservations.Tests.Api;

using System.CommandLine;
using System.Reflection;
using BunkFy.Modules.Reservations.Admin.Contracts;
using BunkFy.Modules.Reservations.AdminApi;
using BunkFy.Modules.Reservations.AdminCli;
using BunkFy.Modules.Reservations.Api;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Administration.Api;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentSurfaceTests
{
    [Fact]
    public void Http_write_contracts_keep_operation_identity_in_the_route()
    {
        AssertProperties<ReservationsModule.AmendReservationStayRequest>(
            "Arrival",
            "Departure",
            "ExpectedArrivalTime",
            "ExpectedDepartureTime",
            "ExpectedDetailsRevision",
            "InventoryUnitIds");
        AssertProperties<ReservationsModule.ReconcileReservationStayAmendmentRequest>(
            "ExpectedOperationVersion");
        AssertProperties<ReservationsAdminApiModule.AmendReservationStayRequest>(
            "Arrival",
            "Confirmed",
            "Departure",
            "ExpectedArrivalTime",
            "ExpectedDepartureTime",
            "ExpectedDetailsRevision",
            "InventoryUnitIds");
        AssertProperties<ReservationsAdminApiModule.ReconcileReservationStayAmendmentRequest>(
            "Confirmed",
            "ExpectedOperationVersion");

        Assert.Null(typeof(ReservationsModule.AmendReservationStayRequest).GetProperty("OperationId"));
        Assert.Null(typeof(ReservationsAdminApiModule.AmendReservationStayRequest).GetProperty("OperationId"));
        Assert.Equal(
            typeof(bool),
            typeof(ReservationsAdminApiModule.AmendReservationStayRequest)
                .GetProperty("Confirmed")?.PropertyType);
        Assert.Equal(
            typeof(bool),
            typeof(ReservationsAdminApiModule.ReconcileReservationStayAmendmentRequest)
                .GetProperty("Confirmed")?.PropertyType);
    }

    [Fact]
    public void Recovery_contract_is_privacy_minimal_and_has_a_total_keyset_cursor()
    {
        AssertProperties<ReservationStayAmendmentRecoveryCursorDto>(
            "OperationId",
            "Outcome",
            "ReservationId",
            "UpdatedAtUtc");
        AssertProperties<ReservationStayAmendmentRecoveryItemDto>(
            "NextRecoveryEligibleAtUtc",
            "OperationId",
            "OperationVersion",
            "Outcome",
            "PropertyId",
            "RecoveryEligible",
            "RequestedAtUtc",
            "ReservationId",
            "UpdatedAtUtc");
        AssertProperties<ReservationStayAmendmentRecoveryPageDto>("NextCursor", "Operations");
        Assert.DoesNotContain(
            typeof(ReservationStayAmendmentRecoveryItemDto).GetProperties(),
            property => property.Name.Contains("Guest", StringComparison.Ordinal) ||
                property.Name.Contains("Email", StringComparison.Ordinal) ||
                property.Name.Contains("Phone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Public_and_admin_routes_publish_dedicated_bounded_contracts()
    {
        RouteEndpoint[] endpoints = await MapEndpointsAsync();
        const string publicBase = "/api/reservations/properties/{propertyId:guid}";
        const string adminBase = "/api/admin/reservations/properties/{propertyId:guid}";
        const string resource = "/{reservationId:guid}/stay-amendments/{operationId:guid}";

        AssertPublicEndpoint<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Put,
            publicBase + resource,
            ReservationsAdminPermissionCodes.Manage);
        AssertPublicEndpoint<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Get,
            publicBase + resource,
            ReservationsAdminPermissionCodes.Read);
        AssertPublicEndpoint<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Post,
            publicBase + resource + "/reconcile",
            ReservationsAdminPermissionCodes.Manage);
        AssertPublicEndpoint<ReservationStayAmendmentRecoveryPageDto>(
            endpoints,
            HttpMethods.Get,
            publicBase + "/stay-amendments/recovery",
            ReservationsAdminPermissionCodes.Read);

        AssertResponse<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Put,
            adminBase + resource);
        AssertResponse<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Get,
            adminBase + resource);
        AssertResponse<ReservationStayAmendmentReceiptDto>(
            endpoints,
            HttpMethods.Post,
            adminBase + resource + "/reconcile");
        AssertResponse<ReservationStayAmendmentRecoveryPageDto>(
            endpoints,
            HttpMethods.Get,
            adminBase + "/stay-amendments/recovery");

        AssertPublicEndpoint<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            publicBase + "/{reservationId:guid}/inventory",
            ReservationsAdminPermissionCodes.Manage);
        AssertResponse<ReservationMutationReceiptDto>(
            endpoints,
            HttpMethods.Put,
            adminBase + "/{reservationId:guid}/inventory");
    }

    [Fact]
    public void Admin_operations_have_stable_audit_names()
    {
        Assert.Equal("reservations.amend-stay", ReservationsAdminOperationNames.AmendStay);
        Assert.Equal("reservations.get-stay-amendment", ReservationsAdminOperationNames.GetStayAmendment);
        Assert.Equal(
            "reservations.list-stay-amendment-recovery",
            ReservationsAdminOperationNames.ListStayAmendmentRecovery);
        Assert.Equal(
            "reservations.reconcile-stay-amendment",
            ReservationsAdminOperationNames.ReconcileStayAmendment);
    }

    [Fact]
    public void Stay_amendment_errors_have_actionable_http_statuses()
    {
        ApiErrorStatusCodeMap publicMap = ErrorMap(typeof(ReservationsModule));
        ApiErrorStatusCodeMap adminMap = ErrorMap(typeof(ReservationsAdminApiModule));

        foreach (ApiErrorStatusCodeMap map in new[] { publicMap, adminMap })
        {
            Assert.Equal(
                StatusCodes.Status404NotFound,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentOperationNotFound));
            Assert.Equal(
                StatusCodes.Status409Conflict,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentOperationConflict));
            Assert.Equal(
                StatusCodes.Status409Conflict,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentOperationVersionConflict));
            Assert.Equal(
                StatusCodes.Status409Conflict,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentReconcileInvalid));
            Assert.Equal(
                StatusCodes.Status429TooManyRequests,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentReconcileTooSoon));
            Assert.Equal(
                StatusCodes.Status400BadRequest,
                map.GetStatusCode(ReservationsApplicationErrors.StayAmendmentRequestInvalid));
        }
    }

    [Fact]
    public void Admin_cli_groups_amend_status_recovery_and_reconcile_with_mutation_confirmation()
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

        Command reservations = Assert.Single(root.Subcommands, command => command.Name == "reservations");
        Command amendments = Assert.Single(
            reservations.Subcommands,
            command => command.Name == "stay-amendments");
        Command amend = Assert.Single(amendments.Subcommands, command => command.Name == "amend");
        Command status = Assert.Single(amendments.Subcommands, command => command.Name == "status");
        Command list = Assert.Single(amendments.Subcommands, command => command.Name == "list-recovery");
        Command reconcile = Assert.Single(
            amendments.Subcommands,
            command => command.Name == "reconcile");

        Assert.True(HasOption(amend, "yes"));
        Assert.True(HasOption(reconcile, "yes"));
        Assert.False(HasOption(status, "yes"));
        Assert.False(HasOption(list, "yes"));
        Assert.True(HasOption(list, "cursor-reservation-id"));
        Assert.True(HasOption(list, "page-size"));

        const string propertyId = "71000000-0000-0000-0000-000000000001";
        const string reservationId = "72000000-0000-0000-0000-000000000001";
        const string operationId = "73000000-0000-0000-0000-000000000001";
        const string unitId = "74000000-0000-0000-0000-000000000001";
        Assert.Empty(root.Parse([
            "reservations", "stay-amendments", "amend",
            "--property-id", propertyId,
            "--reservation-id", reservationId,
            "--operation-id", operationId,
            "--arrival", "2026-10-01",
            "--departure", "2026-10-03",
            "--unit-ids", unitId,
            "--expected-details-revision", "4",
            "--yes"
        ]).Errors);
        Assert.Empty(root.Parse([
            "reservations", "stay-amendments", "status",
            "--property-id", propertyId,
            "--reservation-id", reservationId,
            "--operation-id", operationId
        ]).Errors);
        Assert.Empty(root.Parse([
            "reservations", "stay-amendments", "list-recovery",
            "--property-id", propertyId,
            "--page-size", "25"
        ]).Errors);
        Assert.Empty(root.Parse([
            "reservations", "stay-amendments", "reconcile",
            "--property-id", propertyId,
            "--reservation-id", reservationId,
            "--operation-id", operationId,
            "--expected-operation-version", "2",
            "--yes"
        ]).Errors);
    }

    private static async Task<RouteEndpoint[]> MapEndpointsAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddOptions<ReservationsApiSecurityOptions>();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        builder.Services.AddSingleton<AdminApiExecutor>(_ => null!);
        await using WebApplication app = builder.Build();

        new ReservationsModule().MapEndpoints(app);
        new ReservationsAdminApiModule().MapEndpoints(app);

        return [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
        ];
    }

    private static void AssertPublicEndpoint<TResponse>(
        IReadOnlyCollection<RouteEndpoint> endpoints,
        string method,
        string route,
        string permissionCode)
    {
        RouteEndpoint endpoint = AssertResponse<TResponse>(endpoints, method, route);
        AccessPermissionMetadata permission =
            Assert.Single(endpoint.Metadata.OfType<AccessPermissionMetadata>());
        Assert.Equal(permissionCode, permission.Permission.Value);
        Assert.Equal("reservations-property", permission.ScopeResolverName);
    }

    private static RouteEndpoint AssertResponse<TResponse>(
        IReadOnlyCollection<RouteEndpoint> endpoints,
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

    private static ApiErrorStatusCodeMap ErrorMap(Type moduleType) =>
        Assert.IsType<ApiErrorStatusCodeMap>(moduleType.GetField(
            "ErrorStatusCodes",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));

    private static bool HasOption(Command command, string name) =>
        command.Options.Any(option =>
            string.Equals(option.Name.TrimStart('-'), name, StringComparison.Ordinal) ||
            option.Aliases.Any(alias =>
                string.Equals(alias.TrimStart('-'), name, StringComparison.Ordinal)));

    private static void AssertProperties<T>(params string[] expected)
    {
        string[] actual = typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}
