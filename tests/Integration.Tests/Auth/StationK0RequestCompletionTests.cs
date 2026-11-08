namespace Integration.Tests;

using System.Net;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class StationK0RequestCompletionTests
{
    [Fact]
    public async Task Delivered_response_waits_for_the_same_requests_final_observations()
    {
        StationK0RequestCompletion completion = new();
        StationK0RequestCompletion.Entry? entered = null;
        using HttpClient client = new(completion.CreateHandler(new ControlledTransport(request =>
        {
            DefaultHttpContext context = new();
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                context.Request.Headers[header.Key] = header.Value.ToArray();
            }

            entered = completion.Enter(context);
            return new(HttpStatusCode.Unauthorized);
        })));

        Task<HttpResponseMessage> pending = client.GetAsync("http://localhost/synthetic");
        Assert.NotNull(entered);
        Assert.False(pending.IsCompleted);
        entered.Completed.SetResult();
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Transport_rejection_without_application_entry_does_not_wait_for_observations()
    {
        StationK0RequestCompletion completion = new();
        using HttpClient client = new(completion.CreateHandler(new ControlledTransport(
            _ => new(HttpStatusCode.RequestHeaderFieldsTooLarge))));
        using HttpResponseMessage response = await client.GetAsync("http://localhost/synthetic")
            .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, response.StatusCode);
    }

    private sealed class ControlledTransport(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
