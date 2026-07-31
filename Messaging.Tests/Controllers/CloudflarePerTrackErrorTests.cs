using System.Net;
using System.Text;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Realtime.Sfu;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// The 1:1-call half of the same defect pinned by
/// <c>Guild.Tests/Controllers/CloudflarePerTrackErrorTests.cs</c> - guild voice and DM calls run
/// two copies of this proxy, and the retry loop below is character-for-character the same in both.
/// </summary>
[TestFixture]
public class CloudflarePerTrackErrorTests
{
    private const string CallId = "call-1";
    private const string UserId = "user-1";

    private const string TrackNotFoundBody = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0" },
          "tracks": [
            {
              "trackName": "audio",
              "sessionId": "cf-remote-session",
              "errorCode": "TrackNotFound",
              "errorDescription": "Track not found"
            }
          ]
        }
        """;

    private CountingCloudflareHandler _handler = null!;
    private CloudflareController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new CountingCloudflareHandler(TrackNotFoundBody);
        var cfService = new CloudflareService(
            new SingleHandlerFactory(_handler), NullLogger<CloudflareService>.Instance);
        var cache = new FakeDistributedCache();

        var bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        _controller = new CloudflareController(
            cfService, new FakeMessagingHubContext(), cache,
            new LockedJsonCacheStore(new FakeDistributedLockService(), cache),
            bus, new DeviceIdResolver(bus, cache, NullLogger<DeviceIdResolver>.Instance),
            NullLogger<CloudflareController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(UserId) },
            },
        };
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    /// <summary>Subscribe-only body: all tracks remote, so it takes the retry path.</summary>
    private static TracksNewBody SubscribeBody() => new(
        "cf-local-session",
        new CfSessionDescription("offer", "v=0"),
        [new CfTrackNew("remote", SessionId: "cf-remote-session", TrackName: "audio")]);

    [Test]
    public async Task SubscribeWithAPerTrackError_RetriesLikeAnyOtherTransientCloudflareFailure()
    {
        await _controller.TracksNew(CallId, SubscribeBody(), CancellationToken.None);

        Assert.That(_handler.TracksNewCount, Is.GreaterThan(1),
            "a per-track 'Track not found' is the propagation race the retry loop was added for, "
            + "but arrives as a 200 and so is never retried");
    }

    [Test]
    public async Task SubscribeWithAPerTrackError_DoesNotReturnPlain200ToTheClient()
    {
        var result = await _controller.TracksNew(CallId, SubscribeBody(), CancellationToken.None);

        Assert.That(result, Is.Not.InstanceOf<OkObjectResult>(),
            "the client has no way to tell this apart from a successful subscribe");
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://cloudflare.test/") };
    }

    private sealed class CountingCloudflareHandler(string tracksNewBody) : HttpMessageHandler
    {
        public int TracksNewCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("sessions/new"))
            {
                body = """{"sessionId":"cf-local-session"}""";
            }
            else if (path.EndsWith("tracks/new"))
            {
                TracksNewCount++;
                body = tracksNewBody;
            }
            else
            {
                body = """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[]}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
