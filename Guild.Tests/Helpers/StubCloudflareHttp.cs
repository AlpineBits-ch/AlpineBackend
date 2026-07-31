using System.Net;
using System.Text;
using Echo.Realtime.Sfu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Helpers;

/// <summary>Builds a real <see cref="CloudflareService"/> over a canned HTTP handler.</summary>
internal static class StubCloudflareHttp
{
    public const string SessionId = "cf-session-stub";

    public static CloudflareService CreateService() =>
        new(new StubFactory(), NullLogger<CloudflareService>.Instance);

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler()) { BaseAddress = new Uri("https://cloudflare.test/") };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("sessions/new")
                ? $$"""{"sessionId":"{{SessionId}}"}"""
                : """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
