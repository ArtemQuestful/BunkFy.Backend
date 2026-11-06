namespace BunkFy.Host.ServiceDefaults.Tests.Security.Stations;

using BunkFy.Host.ServiceDefaults.Security.Stations;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StationEndpointInventoryTests
{
    private const string StationAliasPolicy = "station-k0.alias";

    [Fact]
    public async Task Unclassified_route_is_implicitly_forbidden_and_hash_is_deterministic()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/rooms/{roomId}", (string roomId) => Results.Ok(roomId))
            .WithName("rooms.get");

        try
        {
            StationEndpointInventory first = Build(app);
            StationEndpointInventory second = Build(app);

            StationEndpointInventoryEntry endpoint = Assert.Single(first.Payload.Endpoints);
            Assert.Equal("/rooms/{roomId}", endpoint.Identity.RawRoute);
            Assert.Equal(["GET"], endpoint.Identity.Methods);
            Assert.Equal("Forbidden", endpoint.EffectiveClass);
            Assert.Equal("implicit-host-default", endpoint.ClassOrigin);
            Assert.Equal(0, first.EligibleCount);
            Assert.Equal(first.CanonicalJson, second.CanonicalJson);
            Assert.Equal(first.CanonicalSha256, second.CanonicalSha256);
            Assert.Contains("development-swagger-middleware", first.CanonicalJson, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Explicit_eligible_read_requires_named_station_policy()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/eligible", () => Results.NoContent())
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy);

        try
        {
            StationEndpointInventory inventory = Build(app);

            StationEndpointInventoryEntry endpoint = Assert.Single(inventory.Payload.Endpoints);
            Assert.Equal("Eligible", endpoint.EffectiveClass);
            Assert.Equal("read", endpoint.AccessKind);
            Assert.True(endpoint.StationAwarePolicy);
            Assert.Equal(1, inventory.EligibleCount);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Eligible_mutation_without_attribution_adapter_is_rejected()
    {
        WebApplication app = CreateApplication();
        app.MapPost("/eligible", () => Results.NoContent())
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("attribution adapter", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Direct_station_scheme_selection_is_rejected()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/direct", () => Results.NoContent())
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = StationAuthenticationDefaults.SchemeName
            });

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("cannot select BunkFyStation directly", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Non_eligible_endpoint_cannot_name_station_policy()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/forbidden", () => Results.NoContent())
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("non-eligible", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Eligible_endpoint_cannot_allow_anonymous_access()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/anonymous", () => Results.NoContent())
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy)
            .AllowAnonymous();

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("cannot allow anonymous", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Eligible_endpoint_with_real_assurance_metadata_is_rejected()
    {
        WebApplication app = CreateApplication();
        app.MapGet("/assured", () => Results.NoContent())
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy)
            .RequireAuthenticationAssurance(new AuthenticationAssuranceRequirement(
                maxAuthenticationAge: TimeSpan.FromMinutes(5)));

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("assurance", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Named_policy_alias_that_selects_station_is_rejected()
    {
        WebApplication app = CreateApplication(addStationAliasPolicy: true);
        app.MapGet("/alias", () => Results.NoContent())
            .RequireAuthorization(StationAliasPolicy);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Build(app));

            Assert.Contains("resolved authorization policy", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public void Duplicate_canonical_route_identity_is_rejected()
    {
        RouteEndpointBuilder first = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/duplicate"),
            order: 0);
        first.Metadata.Add(new HttpMethodMetadata(["GET"]));
        RouteEndpointBuilder second = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/duplicate"),
            order: 0);
        second.Metadata.Add(new HttpMethodMetadata(["GET"]));
        DefaultEndpointDataSource dataSource = new(first.Build(), second.Build());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            StationEndpointInventoryBuilder.Build([dataSource], "public"));

        Assert.Contains("Duplicate station endpoint", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplication CreateApplication(bool addStationAliasPolicy = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Integration"
        });
        builder.AddBunkFyStationK0();
        builder.AddBunkFyStationK0ValidationPolicies();
        if (addStationAliasPolicy)
        {
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(StationAliasPolicy, policy =>
                {
                    policy.AuthenticationSchemes.Add(StationAuthenticationDefaults.SchemeName);
                    policy.RequireAuthenticatedUser();
                });
        }

        return builder.Build();
    }

    private static StationEndpointInventory Build(WebApplication app) =>
        StationEndpointInventoryBuilder.Build(
            ((IEndpointRouteBuilder)app).DataSources,
            "public",
            app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
}
