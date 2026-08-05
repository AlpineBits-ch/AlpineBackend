using System.Net;
using System.Text;

namespace Social.Tests.Helpers;

/// <summary>
/// An <see cref="IHttpClientFactory"/> that answers from a script instead of the network.
/// </summary>
internal sealed class StubHttpClientFactory(HttpStatusCode status, string body) : IHttpClientFactory
{
    private readonly StubHandler _handler = new(status, body);

    /// <summary>Every request the resolver made, in order, as relative URIs.</summary>
    public IReadOnlyList<string> Requests => _handler.Requests;

    public int CallCount => _handler.Requests.Count;

    /// <summary>Answers 200 with a registry-shaped payload.</summary>
    public static StubHttpClientFactory Returning(string name) =>
        new(HttpStatusCode.OK, $$"""{"id":"1","name":{{System.Text.Json.JsonSerializer.Serialize(name)}}}""");

    public static StubHttpClientFactory NotFound() =>
        new(HttpStatusCode.NotFound, """{"message":"Unknown Application","code":10002}""");

    public static StubHttpClientFactory Failing(HttpStatusCode status) => new(status, "");

    /// <summary>Throws instead of answering - the unreachable-network case, which must not surface
    /// as an exception to the caller.</summary>
    public static StubHttpClientFactory Unreachable() => new(HttpStatusCode.OK, body: null!);

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://discord.test/api/v9/") };

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);

            if (body is null) throw new HttpRequestException("network is down");

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
