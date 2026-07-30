namespace Import.Tests.Helpers;

/// <summary>Hand-rolled IHttpClientFactory that always hands back an HttpClient wired to a
/// caller-supplied HttpMessageHandler - lets DiscordApiClient tests intercept the real
/// discord.com/api/v10 calls without any network access, same convention as Federation.Tests'
/// TestVentaFederationProvider.CreateHttpClient override.</summary>
public class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://discord.com/api/v10/") };
}

/// <summary>Queues canned responses (FIFO) and records every request it sees - a request beyond
/// what was queued repeats the last queued response, so simple "one queued 200" setups don't need
/// an entry per retry attempt.</summary>
public class QueuedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(Func<HttpResponseMessage> factory) => _responses.Enqueue(factory);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var factory = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return Task.FromResult(factory());
    }
}
