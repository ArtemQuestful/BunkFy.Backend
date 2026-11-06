namespace Integration.Tests.Support;

using System.Text.Json;
using BunkFy.Adapters.SmtpEmail;
using BunkFy.Host.Api;
using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Stations.Api;
using Gma.Modules.Notifications.Adapters.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Integration")]
public sealed class ProductCapabilitiesEndpointTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recovery_capability_is_fail_closed_and_follows_host_option(
        bool recoveryEnabled, bool pinEnabled)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.Configure<SmtpEmailOptions>(options =>
            options.Enabled = true);
        builder.Services.Configure<NotificationEmailAdapterOptions>(options =>
            options.Enabled = true);
        builder.Services.Configure<WorkspacesApiSecurityOptions>(options =>
            options.StaffOnboardingRecoveryPostEnabled = recoveryEnabled);
        builder.Services.Configure<StationApiOptions>(options => options.Enabled = pinEnabled);
        await using WebApplication app = builder.Build();
        app.MapBunkFyProductCapabilities();
        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => string.Equals(
                    candidate.RoutePattern.RawText,
                    "/api/product-capabilities",
                    StringComparison.Ordinal) &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?
                    .HttpMethods.Contains(
                        HttpMethods.Get,
                        StringComparer.Ordinal) == true);
        DefaultHttpContext context = new()
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        BunkFyProductCapabilitiesResponse? response = await JsonSerializer
            .DeserializeAsync<BunkFyProductCapabilitiesResponse>(
                context.Response.Body,
                JsonSerializerOptions.Web);
        Assert.NotNull(response);
        Assert.True(response.EmailVerificationEnabled);
        Assert.Equal(
            recoveryEnabled,
            response.WorkspaceStaffOnboardingRecoveryEnabled);
        Assert.Equal(pinEnabled, response.StaffPinEnabled);
    }
}
