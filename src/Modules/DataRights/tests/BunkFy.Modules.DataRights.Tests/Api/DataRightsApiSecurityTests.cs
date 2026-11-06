namespace BunkFy.Modules.DataRights.Tests.Api;

using System.Text.Json;
using BunkFy.Modules.DataRights.Api;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsApiSecurityTests
{
    [Fact]
    public async Task Case_endpoints_use_distinct_scoped_permissions()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<DataRightsApiSecurityOptions>(options =>
        {
            AuthenticationAssuranceRequirement assurance = new(
                ["urn:test:acr:mfa"],
                TimeSpan.FromMinutes(10));
            options.AnonymisationExecutionAssurance = assurance;
            options.RestrictionExecutionAssurance = assurance;
            options.CorrectionExecutionAssurance = assurance;
            options.ExportGenerationAssurance = assurance;
            options.ExportDownloadAssurance = assurance;
        });
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new DataRightsModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string cases = "/api/data-rights/properties/{propertyId:guid}/cases";
        AssertPermission(endpoints, HttpMethods.Get, cases, DataRightsAdminPermissionCodes.Read);
        AssertPermission(endpoints, HttpMethods.Post, cases, DataRightsAdminPermissionCodes.Create);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/discovery",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/subjects",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/review-evidence",
            DataRightsAdminPermissionCodes.Review);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/subjects/discover",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/subjects/select",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/subjects/unselect",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/restriction/release-targets",
            DataRightsAdminPermissionCodes.Discover);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction/release-target",
            DataRightsAdminPermissionCodes.Discover);
        AssertNoAssurance(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/restriction/release-targets");
        AssertNoAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction/release-target");
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/review",
            DataRightsAdminPermissionCodes.Review);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/decision",
            DataRightsAdminPermissionCodes.Decide);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/decision/outcome",
            DataRightsAdminPermissionCodes.Decide);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/cancel",
            DataRightsAdminPermissionCodes.Manage);
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/execution",
            DataRightsAdminPermissionCodes.Read);
        AssertPermissionSet(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/execution",
            (DataRightsAdminPermissionCodes.Erase, "tenant"),
            (DataRightsAdminPermissionCodes.Read, "data-rights-property"));
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/execution");
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction",
            DataRightsAdminPermissionCodes.Restrict);
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction");
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/correction",
            DataRightsAdminPermissionCodes.Execute);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/correction",
            DataRightsAdminPermissionCodes.Execute);
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/correction");
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/export",
            DataRightsAdminPermissionCodes.Export);
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export",
            DataRightsAdminPermissionCodes.Export);
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export");
        AssertPermission(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry",
            DataRightsAdminPermissionCodes.Export);
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry");
        AssertPermission(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/download",
            DataRightsAdminPermissionCodes.DownloadExport);
        AssertAssurance(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/download");
    }

    [Fact]
    public async Task Tenant_case_endpoints_use_only_tenant_scoped_permissions()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<DataRightsApiSecurityOptions>(options =>
        {
            AuthenticationAssuranceRequirement assurance = new(
                maxAuthenticationAge: TimeSpan.FromMinutes(10));
            options.AnonymisationExecutionAssurance = assurance;
            options.CorrectionExecutionAssurance = assurance;
            options.ExportGenerationAssurance = assurance;
            options.ExportDownloadAssurance = assurance;
        });
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new DataRightsModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string cases = "/api/data-rights/tenant/cases";
        (string Method, string Route, string Permission)[] expected =
        [
            (HttpMethods.Get, cases, DataRightsAdminPermissionCodes.Read),
            (HttpMethods.Get, $"{cases}/{{caseId:guid}}", DataRightsAdminPermissionCodes.Read),
            (HttpMethods.Post, cases, DataRightsAdminPermissionCodes.Create),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/requester-verification",
                DataRightsAdminPermissionCodes.Review),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/controller-routing",
                DataRightsAdminPermissionCodes.Review),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/discovery",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/review",
                DataRightsAdminPermissionCodes.Review),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/decision",
                DataRightsAdminPermissionCodes.Decide),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/decision/outcome",
                DataRightsAdminPermissionCodes.Decide),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/cancel",
                DataRightsAdminPermissionCodes.Manage),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/execution",
                DataRightsAdminPermissionCodes.Read),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/execution",
                DataRightsAdminPermissionCodes.Erase),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/subjects",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/review-evidence",
                DataRightsAdminPermissionCodes.Review),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/subjects/discover",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/subjects/select",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/subjects/unselect",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/restriction/release-targets",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/restriction/release-target",
                DataRightsAdminPermissionCodes.Discover),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/correction",
                DataRightsAdminPermissionCodes.Execute),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/correction",
                DataRightsAdminPermissionCodes.Execute),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/export",
                DataRightsAdminPermissionCodes.Export),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/export",
                DataRightsAdminPermissionCodes.Export),
            (
                HttpMethods.Post,
                $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry",
                DataRightsAdminPermissionCodes.Export),
            (
                HttpMethods.Get,
                $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/download",
                DataRightsAdminPermissionCodes.DownloadExport),
        ];

        foreach ((string method, string route, string permissionCode) in expected)
        {
            RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
            AccessPermissionMetadata permission =
                Assert.Single(endpoint.Metadata.OfType<AccessPermissionMetadata>());
            Assert.Equal(permissionCode, permission.Permission.Value);
            Assert.Equal("tenant", permission.ScopeResolverName);
            Assert.DoesNotContain(
                endpoint.Metadata.OfType<AccessPermissionMetadata>(),
                metadata => string.Equals(
                    metadata.ScopeResolverName,
                    "data-rights-property",
                    StringComparison.Ordinal));
        }

        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/execution");
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/correction");
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export");
        AssertAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry");
        AssertAssurance(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/download");
        AssertNoAssurance(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/restriction/release-targets");
        AssertNoAssurance(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction/release-target");
    }

    [Fact]
    public void Sensitive_discovery_responses_are_not_cacheable()
    {
        DefaultHttpContext context = new();

        DataRightsSensitiveResponseHeaders.Apply(context.Response);

        Assert.Equal("no-store, no-cache, max-age=0", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }

    [Fact]
    public void Required_companion_failures_have_distinct_http_semantics()
    {
        (Error Error, int StatusCode)[] expectations =
        [
            (
                DataRightsApplicationErrors.RequiredCompanionUnavailable,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.RequiredCompanionBlocked,
                StatusCodes.Status409Conflict),
            (
                DataRightsApplicationErrors.RequiredCompanionRetryRequired,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.RequiredCompanionResultInvalid,
                StatusCodes.Status500InternalServerError)
        ];

        foreach ((Error error, int statusCode) in expectations)
        {
            Assert.Equal(
                statusCode,
                DataRightsEndpointSupport.ErrorStatusCodes
                    .GetStatusCode(error));
        }
    }

    [Fact]
    public void Subject_owner_failures_have_distinct_http_semantics()
    {
        (Error Error, int StatusCode)[] expectations =
        [
            (
                DataRightsApplicationErrors.DiscoveryScopeUnavailable,
                StatusCodes.Status409Conflict),
            (
                DataRightsApplicationErrors.SubjectOwnerUnavailable,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.SubjectOwnerRetryRequired,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.SubjectOwnerCatalogInvalid,
                StatusCodes.Status500InternalServerError),
            (
                DataRightsApplicationErrors.SubjectOwnerResultInvalid,
                StatusCodes.Status500InternalServerError),
            (
                DataRightsApplicationErrors.SubjectCoordinateInvalid,
                StatusCodes.Status400BadRequest)
        ];

        foreach ((Error error, int statusCode) in expectations)
        {
            Assert.Equal(
                statusCode,
                DataRightsEndpointSupport.ErrorStatusCodes
                    .GetStatusCode(error));
        }
    }

    [Fact]
    public void Export_owner_and_retry_failures_have_distinct_http_semantics()
    {
        (Error Error, int StatusCode)[] expectations =
        [
            (
                DataRightsApplicationErrors.ExportOwnerUnavailable,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.ExportOwnerCatalogInvalid,
                StatusCodes.Status500InternalServerError),
            (
                DataRightsApplicationErrors.ExportArtifactVersionConflict,
                StatusCodes.Status409Conflict)
        ];

        foreach ((Error error, int statusCode) in expectations)
        {
            Assert.Equal(
                statusCode,
                DataRightsEndpointSupport.ErrorStatusCodes
                    .GetStatusCode(error));
        }
    }

    [Fact]
    public void Mutation_owner_failures_have_distinct_http_semantics()
    {
        (Error Error, int StatusCode)[] expectations =
        [
            (
                DataRightsApplicationErrors.RestrictionOwnerUnavailable,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.RestrictionOwnerCatalogInvalid,
                StatusCodes.Status500InternalServerError),
            (
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.RestrictionExecutionBlocked,
                StatusCodes.Status409Conflict),
            (
                DataRightsApplicationErrors.RestrictionOwnerProofInvalid,
                StatusCodes.Status500InternalServerError),
            (
                DataRightsApplicationErrors.CorrectionOwnerUnavailable,
                StatusCodes.Status503ServiceUnavailable),
            (
                DataRightsApplicationErrors.CorrectionOwnerCatalogInvalid,
                StatusCodes.Status500InternalServerError)
        ];

        foreach ((Error error, int statusCode) in expectations)
        {
            Assert.Equal(
                statusCode,
                DataRightsEndpointSupport.ErrorStatusCodes.GetStatusCode(error));
        }
    }

    [Fact]
    public void Restriction_owner_retry_publishes_a_bounded_retry_after()
    {
        DefaultHttpContext context = new();

        IResult result = DataRightsEndpointSupport.ToHttpResult(
            context,
            Result.Failure<DataRightsRestrictionExecutionDto>(
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired));

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false)
                .StatusCode);
        Assert.Equal(
            DataRightsEndpointSupport.ExplicitDependencyRetryAfterSeconds
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.Response.Headers.RetryAfter);
    }

    [Fact]
    public void Case_creation_operation_failures_have_distinct_http_semantics()
    {
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            DataRightsEndpointSupport.ErrorStatusCodes.GetStatusCode(
                DataRightsApplicationErrors.CreationOperationInvalid));
        Assert.Equal(
            StatusCodes.Status409Conflict,
            DataRightsEndpointSupport.ErrorStatusCodes.GetStatusCode(
                DataRightsApplicationErrors.CreationOperationConflict));
    }

    [Fact]
    public async Task Explicit_companion_retry_publishes_a_bounded_retry_after()
    {
        DefaultHttpContext context = new();

        IResult result = await DataRightsEndpointSupport.DispatchAsync(
            context,
            new FixedSubjectResolver(),
            actor => new DispatchTestCommand(actor),
            new FailureDispatcher(
                DataRightsApplicationErrors
                    .RequiredCompanionRetryRequired),
            CancellationToken.None);

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false)
                .StatusCode);
        Assert.Equal(
            DataRightsEndpointSupport
                .ExplicitDependencyRetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            context.Response.Headers.RetryAfter);
    }

    [Fact]
    public void Explicit_subject_owner_retry_publishes_a_bounded_retry_after()
    {
        DefaultHttpContext context = new();

        IResult result = DataRightsEndpointSupport.ToHttpResult(
            context,
            Result.Failure<DataRightsSubjectDiscoveryResponse>(
                DataRightsApplicationErrors.SubjectOwnerRetryRequired));

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false)
                .StatusCode);
        Assert.Equal(
            DataRightsEndpointSupport
                .ExplicitDependencyRetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            context.Response.Headers.RetryAfter);
    }

    [Fact]
    public void Missing_subject_owner_does_not_advertise_an_immediate_retry()
    {
        DefaultHttpContext context = new();

        IResult result = DataRightsEndpointSupport.ToHttpResult(
            context,
            Result.Failure<DataRightsSubjectDiscoveryResponse>(
                DataRightsApplicationErrors.SubjectOwnerUnavailable));

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false)
                .StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Direct_discovery_routes_publish_explicit_retry_headers()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(
            new QueryFailureDispatcher(
                DataRightsApplicationErrors.SubjectOwnerRetryRequired));
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();
        new DataRightsModule().MapEndpoints(app);
        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        (string Route, Guid? PropertyId)[] routes =
        [
            (
                "/api/data-rights/properties/{propertyId:guid}/cases/{caseId:guid}/subjects/discover",
                Guid.NewGuid()),
            (
                "/api/data-rights/tenant/cases/{caseId:guid}/subjects/discover",
                null)
        ];

        foreach ((string route, Guid? propertyId) in routes)
        {
            RouteEndpoint endpoint = FindEndpoint(
                endpoints,
                HttpMethods.Post,
                route);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new DataRightsDiscoveryEndpoints
                    .DiscoverDataRightsSubjectsRequest(
                        RecordId: null,
                        Email: "guest@example.test",
                        Phone: null,
                        Name: null,
                        DateOfBirth: null,
                        AccountSubjectId: null,
                        OwnerKey: null),
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
            context.Features.Set<IHttpRequestBodyDetectionFeature>(
                new RequestBodyDetectionFeature());
            context.Request.RouteValues["caseId"] =
                Guid.NewGuid().ToString("D");
            if (propertyId.HasValue)
            {
                context.Request.RouteValues["propertyId"] =
                    propertyId.Value.ToString("D");
            }

            await endpoint.RequestDelegate!(context);

            context.Response.Body.Position = 0;
            string responseBody = await new StreamReader(
                context.Response.Body).ReadToEndAsync();
            Assert.True(
                context.Response.StatusCode ==
                    StatusCodes.Status503ServiceUnavailable,
                $"Expected HTTP 503 but received {context.Response.StatusCode}: {responseBody}");
            Assert.Equal(
                DataRightsEndpointSupport
                    .ExplicitDependencyRetryAfterSeconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                context.Response.Headers.RetryAfter);
        }
    }

    [Fact]
    public async Task Companion_unavailability_does_not_advertise_an_immediate_retry()
    {
        DefaultHttpContext context = new();

        IResult result = await DataRightsEndpointSupport.DispatchAsync(
            context,
            new FixedSubjectResolver(),
            actor => new DispatchTestCommand(actor),
            new FailureDispatcher(
                DataRightsApplicationErrors
                    .RequiredCompanionUnavailable),
            CancellationToken.None);

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false)
                .StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Public_case_route_groups_apply_the_no_store_policy()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(new CaseListDispatcher());
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();
        new DataRightsModule().MapEndpoints(app);
        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        (string Route, Guid? PropertyId, Guid? CaseId)[] routes =
        [
            (
                "/api/data-rights/properties/{propertyId:guid}/cases",
                Guid.NewGuid(),
                null),
            ("/api/data-rights/tenant/cases", null, null),
            (
                "/api/data-rights/properties/{propertyId:guid}/cases/" +
                "{caseId:guid}/restriction/release-targets",
                Guid.NewGuid(),
                Guid.NewGuid()),
            (
                "/api/data-rights/tenant/cases/{caseId:guid}/restriction/" +
                "release-targets",
                null,
                Guid.NewGuid()),
            (
                "/api/data-rights/properties/{propertyId:guid}/cases/" +
                "{caseId:guid}/review-evidence",
                Guid.NewGuid(),
                Guid.NewGuid()),
            (
                "/api/data-rights/tenant/cases/{caseId:guid}/review-evidence",
                null,
                Guid.NewGuid())
        ];

        foreach ((string route, Guid? propertyId, Guid? caseId) in routes)
        {
            RouteEndpoint endpoint = FindEndpoint(endpoints, HttpMethods.Get, route);
            DefaultHttpContext context = new()
            {
                RequestServices = app.Services,
                Response = { Body = new MemoryStream() }
            };
            context.Request.Method = HttpMethods.Get;
            if (propertyId.HasValue)
            {
                context.Request.RouteValues["propertyId"] = propertyId.Value.ToString("D");
            }
            if (caseId.HasValue)
            {
                context.Request.RouteValues["caseId"] = caseId.Value.ToString("D");
            }

            await endpoint.RequestDelegate!(context);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(
                "no-store, no-cache, max-age=0",
                context.Response.Headers.CacheControl);
            Assert.Equal("no-cache", context.Response.Headers.Pragma);
            Assert.Equal("0", context.Response.Headers.Expires);
        }
    }

    [Fact]
    public async Task Operator_endpoints_publish_their_success_contracts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRequestDispatcher>(_ => null!);
        builder.Services.AddSingleton<IAccessHttpSubjectResolver>(_ => null!);
        await using WebApplication app = builder.Build();

        new DataRightsModule().MapEndpoints(app);

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()];
        const string cases = "/api/data-rights/properties/{propertyId:guid}/cases";
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            cases,
            typeof(DataRightsCaseListResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}",
            typeof(DataRightsCaseDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/subjects/discover",
            typeof(DataRightsSubjectDiscoveryResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/subjects",
            typeof(DataRightsSelectedSubjectsResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/review-evidence",
            typeof(DataRightsSelectedSubjectsResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/subjects/select",
            typeof(DataRightsCaseDto));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/restriction/release-targets",
            typeof(DataRightsRestrictionReleaseTargetListResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction/release-target",
            typeof(DataRightsCaseDto));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/execution",
            typeof(DataRightsExecutionDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/execution",
            typeof(DataRightsExecutionDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/restriction",
            typeof(DataRightsRestrictionExecutionDto));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{cases}/{{caseId:guid}}/export",
            typeof(DataRightsExportArtifactDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export",
            typeof(DataRightsExportArtifactDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{cases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry",
            typeof(DataRightsExportArtifactDto));

        const string tenantCases = "/api/data-rights/tenant/cases";
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{tenantCases}/{{caseId:guid}}/review-evidence",
            typeof(DataRightsSelectedSubjectsResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{tenantCases}/{{caseId:guid}}/execution",
            typeof(DataRightsExecutionDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{tenantCases}/{{caseId:guid}}/execution",
            typeof(DataRightsExecutionDto));
        AssertProduces(
            endpoints,
            HttpMethods.Get,
            $"{tenantCases}/{{caseId:guid}}/restriction/release-targets",
            typeof(DataRightsRestrictionReleaseTargetListResponse));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{tenantCases}/{{caseId:guid}}/restriction/release-target",
            typeof(DataRightsCaseDto));
        AssertProduces(
            endpoints,
            HttpMethods.Post,
            $"{tenantCases}/{{caseId:guid}}/export/{{artifactId:guid}}/retry",
            typeof(DataRightsExportArtifactDto));
    }

    private static void AssertPermission(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        string expectedPermission)
    {
        RouteEndpoint endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(
                candidate.RoutePattern.RawText?.Trim('/'),
                route.Trim('/'),
                StringComparison.Ordinal) &&
            candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method,
                StringComparer.Ordinal) == true);
        AccessPermissionMetadata permission =
            Assert.Single(endpoint.Metadata.OfType<AccessPermissionMetadata>());
        Assert.Equal(expectedPermission, permission.Permission.Value);
        Assert.Equal(
            "data-rights-property",
            permission.ScopeResolverName);
    }

    private static void AssertPermissionSet(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        params (string Permission, string ScopeResolver)[] expected)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        AccessPermissionSetMetadata permissionSet =
            Assert.Single(endpoint.Metadata.OfType<AccessPermissionSetMetadata>());
        Assert.Equal(expected.Length, permissionSet.Requirements.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index].Permission,
                permissionSet.Requirements[index].Permission.Value);
            Assert.Equal(
                expected[index].ScopeResolver,
                permissionSet.Requirements[index].ScopeResolverName);
            Assert.True(permissionSet.Requirements[index].RequireScope);
        }
    }

    private static void AssertAssurance(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        Assert.Contains(
            endpoint.Metadata,
            metadata => string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal));
    }

    private static void AssertNoAssurance(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        Assert.DoesNotContain(
            endpoint.Metadata,
            metadata => string.Equals(
                metadata.GetType().Name,
                "AuthenticationAssuranceMetadata",
                StringComparison.Ordinal));
    }

    private static void AssertProduces(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        Type responseType)
    {
        RouteEndpoint endpoint = FindEndpoint(endpoints, method, route);
        IProducesResponseTypeMetadata response = Assert.Single(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == StatusCodes.Status200OK);
        Assert.Equal(responseType, response.Type);
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

    private sealed class CaseListDispatcher : IRequestDispatcher
    {
        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default)
        {
            if (query is GetDataRightsRestrictionReleaseTargetsQuery)
            {
                return Task.FromResult((Result<TResponse>)(object)Result.Success(
                    new DataRightsRestrictionReleaseTargetListResponse(
                        CaseVersion: 1,
                        Targets: [],
                        LimitReached: false)));
            }

            if (query is GetDataRightsSelectedSubjectsQuery)
            {
                return Task.FromResult((Result<TResponse>)(object)Result.Success(
                    new DataRightsSelectedSubjectsResponse(
                        CaseVersion: 1,
                        Subjects: [])));
            }

            return Task.FromResult((Result<TResponse>)(object)Result.Success(
                new DataRightsCaseListResponse([], 1, 20, HasMore: false)));
        }
    }

    private sealed record DispatchTestCommand(string ActorId) :
        ICommand<DataRightsCaseDto>;

    private sealed class FixedSubjectResolver :
        IAccessHttpSubjectResolver
    {
        public AccessSubject ResolveSubject(HttpContext httpContext) =>
            AccessSubject.User("data-rights-reviewer");
    }

    private sealed class FailureDispatcher(Error error) : IRequestDispatcher
    {
        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<TResponse>(error));

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class QueryFailureDispatcher(Error error) : IRequestDispatcher
    {
        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<TResponse>(error));
    }

    private sealed class RequestBodyDetectionFeature :
        IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
