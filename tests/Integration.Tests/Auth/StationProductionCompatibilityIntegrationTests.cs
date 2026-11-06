namespace Integration.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Integration.Tests.Support;
using Xunit;

[Trait("Category", "Integration")]
public sealed class StationProductionCompatibilityIntegrationTests
{
    [Fact]
    public async Task Public_auth_sse_anonymous_and_openapi_surfaces_keep_their_ordinary_behavior()
    {
        await using NotificationStreamingTestApplication application = new(environmentName: "Development");
        using HttpClient client = application.CreateClient();
        string stationToken = NotificationStreamingTestApplication.CreateStationShapedToken();

        foreach (string path in new[] { "/api/auth/sessions", "/api/notifications/stream" })
        {
            application.ResetStationAuthenticationObservations();
            using HttpResponseMessage baseline = await client.GetAsync(path);
            using HttpResponseMessage station = await SendStationAsync(client, path, stationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, baseline.StatusCode);
            await AssertStableResponseAsync(baseline, station);
            AssertBearerOwnedChallenge(station);
            Assert.Empty(application.StationAuthenticationObservations);
        }

        foreach (string path in new[] { "/api/product-capabilities", "/swagger/v1/swagger.json" })
        {
            application.ResetStationAuthenticationObservations();
            using HttpResponseMessage baseline = await client.GetAsync(path);
            using HttpResponseMessage station = await SendStationAsync(client, path, stationToken);

            Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
            await AssertStableResponseAsync(baseline, station);
            Assert.Empty(application.StationAuthenticationObservations);
        }

        application.ResetStationAuthenticationObservations();
        using HttpResponseMessage baselineSmoke = await client.GetAsync("/api/smoke");
        using HttpResponseMessage stationSmoke = await SendStationAsync(client, "/api/smoke", stationToken);
        Assert.Equal(HttpStatusCode.OK, baselineSmoke.StatusCode);
        Assert.Equal(baselineSmoke.StatusCode, stationSmoke.StatusCode);
        Assert.Equal(
            baselineSmoke.Content.Headers.ContentType?.ToString(),
            stationSmoke.Content.Headers.ContentType?.ToString());
        await AssertEquivalentSmokeAsync(baselineSmoke, stationSmoke);
        Assert.Empty(application.StationAuthenticationObservations);
    }

    [Fact]
    public async Task Admin_protected_surface_keeps_its_ordinary_bearer_challenge()
    {
        await using AdminApiTestApplication application = new(
            "SqlServer",
            "Server=127.0.0.1,1433;Database=unused;User Id=sa;Password=Pass@word1;" +
            "TrustServerCertificate=True;Connect Timeout=1",
            "nats://127.0.0.1:4222",
            disableDatabaseBackgroundServices: true);
        using HttpClient client = application.CreateClient();
        string stationToken = NotificationStreamingTestApplication.CreateStationShapedToken();

        using HttpResponseMessage baseline = await client.GetAsync("/api/admin/audit");
        application.ResetStationAuthenticationObservations();
        using HttpResponseMessage station = await SendStationAsync(client, "/api/admin/audit", stationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, baseline.StatusCode);
        await AssertStableResponseAsync(baseline, station);
        AssertBearerOwnedChallenge(station);
        Assert.Empty(application.StationAuthenticationObservations);
    }

    private static async Task<HttpResponseMessage> SendStationAsync(
        HttpClient client,
        string path,
        string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            StationAuthenticationDefaults.WireScheme,
            token);
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task AssertStableResponseAsync(
        HttpResponseMessage baseline,
        HttpResponseMessage station)
    {
        Assert.Equal(baseline.StatusCode, station.StatusCode);
        Assert.Equal(
            baseline.Content.Headers.ContentType?.ToString(),
            station.Content.Headers.ContentType?.ToString());
        byte[] baselineBody = await baseline.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
        byte[] stationBody = await station.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
        Assert.Equal(SHA256.HashData(baselineBody), SHA256.HashData(stationBody));
    }

    private static void AssertBearerOwnedChallenge(HttpResponseMessage response)
    {
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(
                value.Scheme,
                StationAuthenticationDefaults.WireScheme,
                StringComparison.Ordinal));
    }

    private static async Task AssertEquivalentSmokeAsync(
        HttpResponseMessage baseline,
        HttpResponseMessage station)
    {
        using JsonDocument baselineBody = JsonDocument.Parse(
            await baseline.Content.ReadAsStreamAsync().ConfigureAwait(true));
        using JsonDocument stationBody = JsonDocument.Parse(
            await station.Content.ReadAsStreamAsync().ConfigureAwait(true));
        foreach (string property in new[]
                 {
                     "application",
                     "service",
                     "status",
                     "releaseId",
                     "admissionEvidenceReference"
                 })
        {
            Assert.Equal(
                baselineBody.RootElement.GetProperty(property).GetRawText(),
                stationBody.RootElement.GetProperty(property).GetRawText());
        }
    }
}
