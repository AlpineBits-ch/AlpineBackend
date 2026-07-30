namespace Isle.Tests.Helpers;

/// <summary>Hand-rolled IHttpClientFactory that always hands back an HttpClient wired to a
/// caller-supplied HttpMessageHandler - lets CloudflareService tests intercept the real Cloudflare
/// Calls HTTP calls without any network access. Same convention as Import.Tests'
/// FakeHttpClientFactory / QueuedHttpMessageHandler.</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://cf-fake.local/v1/apps/test-app/") };
}

/// <summary>Queues canned responses (FIFO) and records every request it sees - a request beyond
/// what was queued repeats the last queued response, so simple "one queued 200" setups don't need
/// an entry per retry attempt.</summary>
internal sealed class QueuedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(Func<HttpResponseMessage> factory) => _responses.Enqueue(factory);

    public void EnqueueJson(System.Net.HttpStatusCode statusCode, string json) =>
        Enqueue(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var factory = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return Task.FromResult(factory());
    }
}
