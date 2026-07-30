namespace Messaging.Tests.Helpers;

/// <summary>Minimal IHttpClientFactory for constructing services (e.g. CloudflareService) whose
/// constructor eagerly calls CreateClient() - tests using this must never actually trigger a real
/// HTTP call through the returned client.</summary>
internal sealed class FakeHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
