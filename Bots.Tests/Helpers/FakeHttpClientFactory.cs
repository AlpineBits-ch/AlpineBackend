using System.Net;

namespace Bots.Tests.Helpers;

/// <summary>Hand-rolled HttpMessageHandler stub - returns one canned response regardless of the
/// request, and records the last request so tests can assert on what was sent.</summary>
internal sealed class FakeHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody),
        };
    }
}

/// <summary>Returns a different canned response per call, so a test can distinguish a cache hit
/// from a second real exchange.</summary>
internal sealed class SequencedHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private readonly (HttpStatusCode Status, string Body)[] _responses = responses;

    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Past the end, keep replaying the last response rather than throwing - a test asserting
        // on CallCount shouldn't fail with an index error first.
        var index = Math.Min(CallCount, _responses.Length - 1);
        CallCount++;

        var (status, body) = _responses[index];
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}
