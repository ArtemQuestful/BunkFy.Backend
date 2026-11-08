namespace Integration.Tests.Support;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

// Kestrel may flush a response before outer observation middleware returns.
// Keep each synthetic request's snapshot boundary separate from HTTP delivery.
internal sealed class StationK0RequestCompletion
{
    private const string Header = "X-Station-K0-Test-Completion";
    private readonly ConcurrentDictionary<string, Entry> pending = new(StringComparer.Ordinal);

    public Entry? Enter(HttpContext context)
    {
        if (!this.pending.TryGetValue(context.Request.Headers[Header].ToString(), out Entry? entry))
        {
            return null;
        }

        Volatile.Write(ref entry.Entered, true);
        return entry;
    }

    public HttpMessageHandler CreateHandler(HttpMessageHandler transport) => new CompletionHandler(this, transport);

    internal sealed class Entry
    {
        public bool Entered;

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CompletionHandler(StationK0RequestCompletion owner, HttpMessageHandler transport)
        : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string id = Guid.NewGuid().ToString("N");
            Entry entry = new();
            owner.pending[id] = entry;
            request.Headers.Add(Header, id);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref entry.Entered))
                {
                    await entry.Completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return response;
            }
            catch
            {
                response?.Dispose();
                throw;
            }
            finally
            {
                owner.pending.TryRemove(id, out _);
                request.Headers.Remove(Header);
            }
        }
    }
}
