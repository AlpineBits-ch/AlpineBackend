using System.Net;
using System.Text;
using Echo.Realtime.Sfu;
using Echo.Voice.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Transport;

/// <summary>
/// The translation boundary: neutral contracts in, Cloudflare's wire format out, and Cloudflare's
/// failures back as neutral ones.
/// </summary>
[TestFixture]
public class CloudflareMediaTransportTests
{
    private const string SessionBody = """{"sessionId":"cf-minted"}""";

    private static CloudflareMediaTransport TransportFor(RecordingHandler handler) =>
        new(new CloudflareService(new SingleHandlerFactory(handler), NullLogger<CloudflareService>.Instance));

    // ── Vocabulary ────────────────────────────────────────────────────────────

    [Test]
    public void The_backend_is_named_so_a_client_can_branch_without_guessing()
    {
        using var handler = new RecordingHandler(SessionBody);
        Assert.That(TransportFor(handler).Backend, Is.EqualTo("cloudflare"));
    }

    /// <summary>
    /// Cloudflare calls a published track "local" and a pulled one "remote", which describes where
    /// the media sits rather than what the caller is doing.
    /// </summary>
    [Test]
    public async Task Publish_and_subscribe_map_onto_local_and_remote()
    {
        using var handler = new RecordingHandler(SessionBody,
            """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[{"trackName":"audio","mid":"0","location":"local"}],"requiresImmediateRenegotiation":false}""");
        var transport = TransportFor(handler);

        await transport.PublishAsync("cf-1", new VoiceNegotiateRequest(
            new VoiceSessionDescription("offer", "v=0"),
            [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")]));

        Assert.That(handler.LastBody, Does.Contain("\"location\":\"local\""));
        Assert.That(handler.LastBody, Does.Not.Contain("\"direction\""),
            "the neutral vocabulary must not leak to Cloudflare, which would reject it");
    }

    [Test]
    public async Task A_subscribe_carries_the_peers_session_as_Cloudflares_sessionId()
    {
        using var handler = new RecordingHandler(SessionBody,
            """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[{"trackName":"audio","mid":"0","location":"remote"}],"requiresImmediateRenegotiation":false}""");
        var transport = TransportFor(handler);

        await transport.SubscribeAsync("cf-1", new VoiceNegotiateRequest(
            new VoiceSessionDescription("offer", "v=0"),
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-peer", TrackName: "audio")]));

        Assert.Multiple(() =>
        {
            Assert.That(handler.LastBody, Does.Contain("\"location\":\"remote\""));
            Assert.That(handler.LastBody, Does.Contain("\"sessionId\":\"cf-peer\""));
            Assert.That(handler.LastBody, Does.Not.Contain("mediaSessionId"));
        });
    }

    [Test]
    public async Task Results_come_back_in_neutral_vocabulary()
    {
        using var handler = new RecordingHandler(SessionBody,
            """{"sessionDescription":{"type":"answer","sdp":"v=1"},"tracks":[{"trackName":"audio","mid":"7","sessionId":"cf-peer","location":"remote"}],"requiresImmediateRenegotiation":true}""");

        var result = await TransportFor(handler).SubscribeAsync("cf-1", new VoiceNegotiateRequest(
            new VoiceSessionDescription("offer", "v=0"),
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-peer", TrackName: "audio")]));

        var track = result.Tracks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.SessionDescription.Sdp, Is.EqualTo("v=1"));
            Assert.That(result.RequiresImmediateRenegotiation, Is.True);
            Assert.That(track.Mid, Is.EqualTo("7"));
            Assert.That(track.MediaSessionId, Is.EqualTo("cf-peer"));
            Assert.That(track.Direction, Is.EqualTo(VoiceTrackDirection.Subscribe));
        });
    }

    // ── Failure translation ───────────────────────────────────────────────────

    /// <summary>
    /// A caller mapping this to a 502 must not have to catch a Cloudflare-specific exception, or
    /// swapping the SFU would silently turn every handled failure into an unhandled one.
    /// </summary>
    [Test]
    public void A_transport_failure_surfaces_as_a_neutral_exception()
    {
        using var handler = new RecordingHandler(SessionBody, """{"bad":true}""", HttpStatusCode.BadRequest);

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() =>
            TransportFor(handler).PublishAsync("cf-1", new VoiceNegotiateRequest(
                new VoiceSessionDescription("offer", "v=0"),
                [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")])));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Operation, Is.EqualTo("tracks/new"));
            Assert.That(ex.Detail, Does.Contain("bad"), "the underlying detail survives for the log");
            Assert.That(ex.InnerException, Is.InstanceOf<CloudflareCallsException>(),
                "and the original is kept, so nothing is lost by translating");
        });
    }

    /// <summary>A per-track error arrives inside an HTTP 200. It must still translate to a
    /// failure, which is the defect that made a dead subscribe look like a working one.</summary>
    [Test]
    public void A_per_track_error_inside_a_200_is_still_a_failure()
    {
        using var handler = new RecordingHandler(SessionBody,
            """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[{"trackName":"audio","sessionId":"cf-peer","errorCode":"TrackNotFound","errorDescription":"Track not found"}],"requiresImmediateRenegotiation":false}""");

        Assert.ThrowsAsync<VoiceMediaException>(() =>
            TransportFor(handler).PublishAsync("cf-1", new VoiceNegotiateRequest(
                new VoiceSessionDescription("offer", "v=0"),
                [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")])));
    }

    /// <summary>The exact body Cloudflare returned during the 2026-08-07 incident.</summary>
    [Test]
    public void Not_found_track_error_classifies_as_TrackNotFound()
    {
        using var handler = new RecordingHandler(SessionBody,
            """{"requiresImmediateRenegotiation":false,"tracks":[{"sessionId":"1aa4082e","trackName":"screen-7c41c31c","mid":"","errorCode":"not_found_track_error","errorDescription":"Track not found on remote peer. Make sure the publisher peer is connected and sending packets for this track"}]}""");

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() =>
            TransportFor(handler).PublishAsync("cf-1", new VoiceNegotiateRequest(
                new VoiceSessionDescription("offer", "v=0"),
                [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")])));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.TrackNotFound));
    }

    [Test]
    public void A_rejected_request_is_not_mistaken_for_a_missing_track()
    {
        using var handler = new RecordingHandler(SessionBody, """{"errorDescription":"bad offer"}""", HttpStatusCode.BadRequest);

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() =>
            TransportFor(handler).PublishAsync("cf-1", new VoiceNegotiateRequest(
                new VoiceSessionDescription("offer", "v=0"),
                [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")])));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.Rejected));
    }

    [Test]
    public void A_transport_side_fault_classifies_as_Unavailable()
    {
        using var handler = new RecordingHandler(SessionBody, """{"err":"boom"}""", HttpStatusCode.BadGateway);

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() =>
            TransportFor(handler).PublishAsync("cf-1", new VoiceNegotiateRequest(
                new VoiceSessionDescription("offer", "v=0"),
                [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio")])));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.Unavailable));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://cloudflare.test/") };
    }

    private sealed class RecordingHandler(
        string sessionBody, string? negotiateBody = null, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var path = request.RequestUri!.AbsolutePath;
            var isSession = path.EndsWith("sessions/new");

            return new HttpResponseMessage(isSession ? HttpStatusCode.OK : status)
            {
                Content = new StringContent(
                    isSession ? sessionBody : negotiateBody ?? "{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
