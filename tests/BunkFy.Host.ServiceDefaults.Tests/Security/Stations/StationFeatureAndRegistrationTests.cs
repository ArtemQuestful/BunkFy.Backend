namespace BunkFy.Host.ServiceDefaults.Tests.Security.Stations;

using BunkFy.Host.ServiceDefaults.Security.Stations;
using Gma.Framework.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StationFeatureAndRegistrationTests
{
    [Theory]
    [InlineData(null, null, false, 0)]
    [InlineData("not-bool", "not-long", false, 0)]
    [InlineData("true", "0", false, 0)]
    [InlineData("false", "7", false, 7)]
    [InlineData("true", "7", true, 7)]
    [InlineData("true", "-1", false, 0)]
    public void Feature_state_is_fail_closed(
        string? enabled,
        string? epoch,
        bool expectedEnabled,
        long expectedEpoch)
    {
        ConfigurationManager configuration = new();
        configuration["Station:Enabled"] = enabled;
        configuration["Station:Epoch"] = epoch;
        ConfigurationStationFeatureState state = new(configuration);

        StationFeatureSnapshot snapshot = state.Capture();

        Assert.Equal(expectedEnabled, snapshot.Enabled);
        Assert.Equal(expectedEpoch, snapshot.Epoch);
    }

    [Fact]
    public async Task Registration_is_idempotent_and_does_not_replace_bearer_default()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiSecurityDefaults();
        builder.Services.AddAuthentication("Bearer");

        builder.AddBunkFyStationK0();
        builder.AddBunkFyStationK0();

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();
        AuthenticationOptions options = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        AuthenticationSchemeProvider schemes = Assert.IsType<AuthenticationSchemeProvider>(
            provider.GetRequiredService<IAuthenticationSchemeProvider>());
        AuthenticationScheme? stationScheme = await schemes
            .GetSchemeAsync(StationAuthenticationDefaults.SchemeName)
            .ConfigureAwait(true);

        Assert.Equal("Bearer", options.DefaultScheme);
        Assert.NotNull(stationScheme);
        Assert.Equal(typeof(StationAuthenticationHandler), stationScheme.HandlerType);
        Assert.DoesNotContain("JwtBearer", stationScheme.HandlerType?.FullName, StringComparison.Ordinal);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType.Name == "StationK0RegistrationMarker");
        Assert.IsType<StationAuthorizationMiddlewareResultHandler>(Assert.Single(
            provider.GetServices<IAuthorizationMiddlewareResultHandler>()));

        IAuthorizationPolicyProvider policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.Null(await policies.GetPolicyAsync(StationAuthenticationDefaults.StationOnlyPolicy));
        Assert.Null(await policies.GetPolicyAsync(StationAuthenticationDefaults.DualPolicy));
    }

    [Fact]
    public void Registration_rejects_a_preexisting_custom_result_handler_without_discarding_it()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiSecurityDefaults();
        builder.Services.AddAuthentication("Bearer");
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, TestAuthorizationResultHandler>();
        ServiceDescriptor[] before = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler))
            .ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddBunkFyStationK0());

        Assert.Contains("cannot replace a custom", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            before,
            builder.Services
                .Where(descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler))
                .ToArray());
        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType.Name == "StationK0RegistrationMarker");
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    public void Registration_rejects_custom_instance_and_factory_result_handlers_without_mutation(
        string registrationKind)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiSecurityDefaults();
        builder.Services.AddAuthentication("Bearer");
        TestAuthorizationResultHandler custom = new();
        if (string.Equals(registrationKind, "instance", StringComparison.Ordinal))
        {
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler>(custom);
        }
        else
        {
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler>(_ => custom);
        }

        ServiceDescriptor[] before = builder.Services.ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddBunkFyStationK0());

        Assert.Contains("cannot replace a custom", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, builder.Services.ToArray());
        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType.Name == "StationK0RegistrationMarker");
    }

    [Fact]
    public void Registration_rejects_multiple_framework_default_result_handlers_without_mutation()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiSecurityDefaults();
        builder.Services.AddAuthentication("Bearer");
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationMiddlewareResultHandler>();
        ServiceDescriptor[] before = builder.Services.ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddBunkFyStationK0());

        Assert.Contains("cannot replace a custom", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, builder.Services.ToArray());
    }

    [Fact]
    public async Task Runtime_validation_rejects_a_result_handler_registered_after_station()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiSecurityDefaults();
        builder.Services.AddAuthentication("Bearer");
        builder.AddBunkFyStationK0();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, TestAuthorizationResultHandler>();

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationSecurityExtensions.ValidateAuthorizationResultHandlerOwnership(provider));

        Assert.Contains("exclusive ownership", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, provider.GetServices<IAuthorizationMiddlewareResultHandler>().Count());
    }

    [Fact]
    public void Runtime_validation_rejects_zero_result_handlers()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationSecurityExtensions.ValidateAuthorizationResultHandlerOwnership(provider));

        Assert.Contains("exclusive ownership", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_validation_rejects_exactly_one_wrong_result_handler()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IAuthorizationMiddlewareResultHandler, TestAuthorizationResultHandler>()
            .BuildServiceProvider();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationSecurityExtensions.ValidateAuthorizationResultHandlerOwnership(provider));

        Assert.Contains("exclusive ownership", exception.Message, StringComparison.Ordinal);
        Assert.IsType<TestAuthorizationResultHandler>(Assert.Single(
            provider.GetServices<IAuthorizationMiddlewareResultHandler>()));
    }

    [Theory]
    [InlineData("challenge")]
    [InlineData("forbid")]
    public async Task Ordinary_authorization_failures_delegate_to_the_framework_result_handler(
        string resultKind)
    {
        RecordingAuthenticationService authentication = new();
        RecordingStationAuthorizationResponseWriter writer = new();
        RecordingStationAuthorizationObserver observer = new();
        StationAuthorizationMiddlewareResultHandler handler = new(writer, observer);
        DefaultHttpContext context = CreateHttpContext(authentication);
        AuthorizationPolicy policy = new(
            [new DenyAnonymousAuthorizationRequirement()],
            ["Bearer"]);
        PolicyAuthorizationResult result = string.Equals(resultKind, "challenge", StringComparison.Ordinal)
            ? PolicyAuthorizationResult.Challenge()
            : PolicyAuthorizationResult.Forbid();

        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, result);

        Assert.Equal(resultKind == "challenge" ? 1 : 0, authentication.ChallengeCount);
        Assert.Equal(resultKind == "forbid" ? 1 : 0, authentication.ForbidCount);
        Assert.Equal(["Bearer"], authentication.Schemes);
        Assert.Empty(writer.Results);
        Assert.Empty(observer.Observations);
    }

    [Fact]
    public async Task Ordinary_authorization_success_delegates_to_the_next_middleware()
    {
        RecordingAuthenticationService authentication = new();
        RecordingStationAuthorizationResponseWriter writer = new();
        RecordingStationAuthorizationObserver observer = new();
        StationAuthorizationMiddlewareResultHandler handler = new(writer, observer);
        DefaultHttpContext context = CreateHttpContext(authentication);
        AuthorizationPolicy policy = new(
            [new DenyAnonymousAuthorizationRequirement()],
            ["Bearer"]);
        int nextCalls = 0;

        await handler.HandleAsync(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            context,
            policy,
            PolicyAuthorizationResult.Success());

        Assert.Equal(1, nextCalls);
        Assert.Equal(0, authentication.ChallengeCount);
        Assert.Equal(0, authentication.ForbidCount);
        Assert.Empty(writer.Results);
        Assert.Empty(observer.Observations);
    }

    [Theory]
    [InlineData(false, "challenge")]
    [InlineData(true, "forbidden")]
    public async Task Station_authorization_failures_use_the_station_response_contract(
        bool authenticated,
        string expectedName)
    {
        StationAuthorizationResultKind expected = string.Equals(
            expectedName,
            "challenge",
            StringComparison.Ordinal)
            ? StationAuthorizationResultKind.Challenge
            : StationAuthorizationResultKind.Forbidden;
        RecordingAuthenticationService authentication = new();
        RecordingStationAuthorizationResponseWriter writer = new();
        RecordingStationAuthorizationObserver observer = new();
        StationAuthorizationMiddlewareResultHandler handler = new(writer, observer);
        DefaultHttpContext context = CreateHttpContext(authentication);
        if (authenticated)
        {
            context.User.AddIdentity(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("sub", "actor")],
                "Bearer"));
        }

        AuthorizationPolicy policy = new(
            [StationAdmissionRequirement.Instance],
            [StationAuthenticationDefaults.SchemeName]);

        await handler.HandleAsync(
            _ => Task.CompletedTask,
            context,
            policy,
            authenticated ? PolicyAuthorizationResult.Forbid() : PolicyAuthorizationResult.Challenge());

        Assert.Equal([expected], writer.Results);
        StationAuthorizationObservation observation = Assert.Single(observer.Observations);
        Assert.Equal(expected, observation.Result);
        Assert.Equal(authenticated ? 1 : 0, observation.AuthenticatedIdentityCount);
        Assert.Equal(0, authentication.ChallengeCount);
        Assert.Equal(0, authentication.ForbidCount);
    }

    [Fact]
    public async Task Station_response_writer_leaves_an_already_started_response_untouched()
    {
        FeatureCollection features = new();
        StartedResponseFeature responseFeature = new();
        features.Set<IHttpResponseFeature>(responseFeature);
        DefaultHttpContext context = new(features);
        StationAuthorizationResponseWriter writer = new();

        await writer.WriteChallengeAsync(context);
        await writer.WriteForbiddenAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers);
        Assert.Equal(0, responseFeature.Body.Length);
    }

    private static DefaultHttpContext CreateHttpContext(IAuthenticationService authentication)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services
        };
    }

    private sealed class TestAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult) => Task.CompletedTask;
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public int ChallengeCount { get; private set; }

        public int ForbidCount { get; private set; }

        public List<string?> Schemes { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            this.ChallengeCount++;
            this.Schemes.Add(scheme);
            return Task.CompletedTask;
        }

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            this.ForbidCount++;
            this.Schemes.Add(scheme);
            return Task.CompletedTask;
        }

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            System.Security.Claims.ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class RecordingStationAuthorizationResponseWriter : IStationAuthorizationResponseWriter
    {
        public List<StationAuthorizationResultKind> Results { get; } = [];

        public Task WriteChallengeAsync(HttpContext context)
        {
            this.Results.Add(StationAuthorizationResultKind.Challenge);
            return Task.CompletedTask;
        }

        public Task WriteForbiddenAsync(HttpContext context)
        {
            this.Results.Add(StationAuthorizationResultKind.Forbidden);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStationAuthorizationObserver : IStationAuthorizationObserver
    {
        public List<StationAuthorizationObservation> Observations { get; } = [];

        public void Observe(StationAuthorizationObservation observation) =>
            this.Observations.Add(observation);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status202Accepted;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => true;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }
}
