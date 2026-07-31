using System.Net;
using System.Text;
using Echo.Realtime.Sfu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Sfu;

/// <summary>
/// <see cref="CloudflareService.SubscribeTracksAsync"/> — the retry that covers the window between
/// a publisher being announced and that publisher actually sending.
/// </summary>
[TestFixture]
public class SubscribeRetryTests
{
    /// <summary>Cloudflare's "the publisher isn't sending yet" answer: HTTP 200, a valid session
    /// description, and the failure reported on the track entry.</summary>
    private const string NotSending = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0" },
          "tracks": [
            {
              "trackName": "audio",
              "sessionId": "cf-remote-session",
              "mid": "",
              "errorCode": "not_found_track_error",
              "errorDescription": "Track not found on remote peer. Make sure the publisher peer is connected and sending packets for this track"
            }
          ]
        }
        """;

    private const string Sending = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0" },
          "tracks": [ { "trackName": "audio", "mid": "1", "sessionId": "cf-remote-session" } ]
        }
        """;

    private static CfTracksNewRequest SubscribeRequest() => new(
        new CfSessionDescription("offer", "v=0"),
        [new CfTrackNew("remote", SessionId: "cf-remote-session", TrackName: "audio")]);

    private static CloudflareService ServiceFor(ScriptedCloudflareHandler handler) =>
        new(new SingleHandlerFactory(handler), NullLogger<CloudflareService>.Instance);

    [Test]
    public async Task Subscribe_WhenThePublisherStartsSendingMidRetry_Succeeds()
    {
        // The whole point: the track does not exist for the first two pulls and then does.
        using var handler = new ScriptedCloudflareHandler(
            (HttpStatusCode.OK, NotSending),
            (HttpStatusCode.OK, NotSending),
            (HttpStatusCode.OK, Sending));

        var result = await ServiceFor(handler).SubscribeTracksAsync("cf-local-session", SubscribeRequest());

        Assert.Multiple(() =>
        {
            Assert.That(result.Tracks[0].Mid, Is.EqualTo("1"));
            Assert.That(handler.TracksNewCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Subscribe_WhenThePublisherNeverSends_GivesUpAfterTheWholeSchedule()
    {
        // Deliberately real time - the schedule is the thing under test, and faking it here would
        // assert the shape of the loop rather than the budget the loop actually spends.
        using var handler = new ScriptedCloudflareHandler((HttpStatusCode.OK, NotSending));

        Assert.That(
            async () => await ServiceFor(handler).SubscribeTracksAsync("cf-local-session", SubscribeRequest()),
            Throws.TypeOf<CloudflareCallsException>());

        // One attempt, then one per delay. A publisher who never arrives must not retry forever.
        Assert.That(handler.TracksNewCount,
            Is.EqualTo(CloudflareService.SubscribeRetryDelays.Count + 1));
    }

    [Test]
    public void Subscribe_CoversAWholeWebRtcHandshake()
    {
        // The old budget was 250+500+750 = 1.5s, tuned for Cloudflare's internal propagation alone.
        Assert.That(
            CloudflareService.SubscribeRetryDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d),
            Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void Subscribe_StartsRetryingQuickly()
    {
        // The tail is long, but the common case must still resolve in milliseconds: a track that is
        // one propagation hop away should not wait on a schedule sized for the worst case.
        Assert.That(CloudflareService.SubscribeRetryDelays[0],
            Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(250)));
    }

    [Test]
    public async Task Subscribe_WhenCloudflareRejectsTheRequest_DoesNotRetry()
    {
        // A 4xx is a malformed offer, an unknown session or a bad token.
        using var handler = new ScriptedCloudflareHandler(
            (HttpStatusCode.BadRequest, """{"errorDescription":"invalid sdp"}"""));

        Assert.That(
            async () => await ServiceFor(handler).SubscribeTracksAsync("cf-local-session", SubscribeRequest()),
            Throws.TypeOf<CloudflareCallsException>());

        Assert.That(handler.TracksNewCount, Is.EqualTo(1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Subscribe_WhenCloudflareItselfFails_Retries()
    {
        // A 5xx is Cloudflare having a bad moment, not a bad request - the one non-2xx worth
        // trying again.
        using var handler = new ScriptedCloudflareHandler(
            (HttpStatusCode.BadGateway, "upstream unavailable"),
            (HttpStatusCode.OK, Sending));

        var result = await ServiceFor(handler).SubscribeTracksAsync("cf-local-session", SubscribeRequest());

        Assert.Multiple(() =>
        {
            Assert.That(result.Tracks[0].Mid, Is.EqualTo("1"));
            Assert.That(handler.TracksNewCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Subscribe_ThatWorksFirstTime_IsNotRetried()
    {
        using var handler = new ScriptedCloudflareHandler((HttpStatusCode.OK, Sending));

        await ServiceFor(handler).SubscribeTracksAsync("cf-local-session", SubscribeRequest());

        Assert.That(handler.TracksNewCount, Is.EqualTo(1));
    }

    // ── Fixture plumbing ──────────────────────────────────────────────────────

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://cloudflare.test/") };
    }

    /// <summary>
    /// Answers successive tracks/new calls from a script, repeating the last entry once exhausted.
    /// </summary>
    private sealed class ScriptedCloudflareHandler(params (HttpStatusCode Status, string Body)[] script)
        : HttpMessageHandler
    {
        public int TracksNewCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("tracks/new"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[]}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            var step = script[Math.Min(TracksNewCount, script.Length - 1)];
            TracksNewCount++;
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
