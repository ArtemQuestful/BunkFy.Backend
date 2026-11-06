namespace Integration.Tests;

using System.Net;
using System.Net.Http.Headers;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Gma.Framework.Notifications;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

public sealed class NotificationStreamingIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sse_stream_delivers_authenticated_user_notifications()
    {
        await using NotificationStreamingTestApplication application = new();
        using HttpClient client = application.CreateClient();
        string token = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/notifications/stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", "tenant-a");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> responseTask = client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        using HttpResponseMessage response = await responseTask.WaitAsync(timeout.Token);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using StreamReader reader = new(stream);
        await ReadDataLineContainingAsync(reader, "\"kind\":\"heartbeat\"", timeout.Token);

        await application.PublishAsync(
            "tenant-a",
            "user-a",
            "SSE message",
            NotificationSeverity.Warning,
            timeout.Token);
        string dataLine = await ReadDataLineContainingAsync(reader, "SSE message", timeout.Token);

        Assert.Contains("SSE message", dataLine, StringComparison.Ordinal);
        Assert.Contains("streaming.test", dataLine, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"notification\"", dataLine, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"warning\"", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sse_stream_rejects_token_tenant_mismatch()
    {
        await using NotificationStreamingTestApplication application = new();
        using HttpClient client = application.CreateClient();
        string token = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/notifications/stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", "tenant-b");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sse_stream_uses_local_default_tenant_when_tenancy_is_disabled()
    {
        await using NotificationStreamingTestApplication application = new(tenancyEnabled: false);
        using HttpClient client = application.CreateClient();
        string token = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/notifications/stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> responseTask = client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        using HttpResponseMessage response = await responseTask.WaitAsync(timeout.Token);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using StreamReader reader = new(stream);
        await ReadDataLineContainingAsync(reader, "\"kind\":\"heartbeat\"", timeout.Token);

        await application.PublishAsync(
            "default",
            "user-a",
            "Default tenant SSE message",
            cancellationToken: timeout.Token);
        string dataLine = await ReadDataLineContainingAsync(reader, "Default tenant SSE message", timeout.Token);

        Assert.Contains("Default tenant SSE message", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignalR_hub_delivers_authenticated_user_notifications()
    {
        await using NotificationStreamingTestApplication application = new();
        string token = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        TaskCompletionSource<UserNotificationMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(
                    application.Server.BaseAddress,
                    $"/hubs/notifications?access_token={Uri.EscapeDataString(token)}"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => application.Server.CreateHandler();
                })
            .Build();
        connection.On<UserNotificationMessage>(
            "notification",
            message => received.TrySetResult(message));

        await connection.StartAsync();
        await application.WaitForSignalRConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await application.PublishAsync(
            "tenant-a",
            "user-a",
            "SignalR message",
            severity: NotificationSeverity.Success);

        UserNotificationMessage message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("SignalR message", message.Title);
        Assert.Equal("tenant-a", message.ScopeId);
        Assert.Equal("user-a", message.UserId);
        Assert.Equal(NotificationSeverity.Success, message.Severity);
        Assert.Empty(application.StationAuthenticationObservations);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignalR_query_token_is_owned_only_by_the_ordinary_bearer_scheme()
    {
        await using NotificationStreamingTestApplication application = new();
        using HttpClient client = application.CreateClient();
        string ordinaryToken = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        string stationToken = NotificationStreamingTestApplication.CreateStationShapedToken();

        using (HttpResponseMessage ordinary = await client.PostAsync(
                   $"/hubs/notifications/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(ordinaryToken)}",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
            Assert.Empty(application.StationAuthenticationObservations);
        }

        application.ResetStationAuthenticationObservations();
        using HttpResponseMessage station = await client.PostAsync(
            $"/hubs/notifications/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(stationToken)}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, station.StatusCode);
        Assert.Empty(application.StationAuthenticationObservations);
        Assert.Contains(
            station.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            station.Headers.WwwAuthenticate,
            static value => string.Equals(
                value.Scheme,
                StationAuthenticationDefaults.WireScheme,
                StringComparison.Ordinal));

        application.ResetStationAuthenticationObservations();
        using HttpRequestMessage stationHeaderRequest = new(
            HttpMethod.Post,
            "/hubs/notifications/negotiate?negotiateVersion=1");
        stationHeaderRequest.Headers.Authorization = new AuthenticationHeaderValue(
            StationAuthenticationDefaults.WireScheme,
            stationToken);
        using HttpResponseMessage stationHeader = await client.SendAsync(stationHeaderRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, stationHeader.StatusCode);
        Assert.Empty(application.StationAuthenticationObservations);
        Assert.Contains(
            stationHeader.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            stationHeader.Headers.WwwAuthenticate,
            static value => string.Equals(
                value.Scheme,
                StationAuthenticationDefaults.WireScheme,
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignalR_hub_rejects_missing_tenant_claim_when_tenancy_is_enabled()
    {
        await using NotificationStreamingTestApplication application = new();
        string token = NotificationStreamingTestApplication.CreateAccessToken(null, "user-a");
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(application.Server.BaseAddress, "/hubs/notifications"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => application.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignalR_hub_uses_local_default_tenant_when_tenancy_is_disabled()
    {
        await using NotificationStreamingTestApplication application = new(tenancyEnabled: false);
        string token = NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        TaskCompletionSource<UserNotificationMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(application.Server.BaseAddress, "/hubs/notifications"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => application.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();
        connection.On<UserNotificationMessage>(
            "notification",
            message => received.TrySetResult(message));

        await connection.StartAsync();
        await application.WaitForSignalRConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await application.PublishAsync("default", "user-a", "Default tenant SignalR message");

        UserNotificationMessage message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Default tenant SignalR message", message.Title);
        Assert.Equal("default", message.ScopeId);
        Assert.Equal("user-a", message.UserId);
    }

    private static async Task<string> ReadDataLineContainingAsync(
        StreamReader reader,
        string expectedText,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) &&
                line.Contains(expectedText, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException($"No SSE data line containing '{expectedText}' was received.");
    }
}
