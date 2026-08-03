using Echo.Realtime.Sfu;
using Isle.Api.Endpoints;
using Isle.Api.Services.State;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers VoiceCloudflareEndpoints - the WebRTC signalling relay for Isle proximity voice. Called
/// directly as plain C# methods (an instance class, unlike the static endpoint classes elsewhere in
/// this service) with a real <see cref="CloudflareService"/> wired to a fake HttpMessageHandler
/// (same convention as Import.Tests' DiscordApiClient tests) so Cloudflare's HTTP responses are
/// fully controlled, plus an NSubstitute <see cref="ISfuClient"/> and the concrete in-memory
/// voice-state collaborators (VoicePlayerRegistry, VoiceTrackRegistry, VoiceCluster).
/// </summary>
[TestFixture]
public class VoiceCloudflareEndpointsTests
{
    private const string UserId = "user-1";

    private VoicePlayerRegistry _registry = null!;
    private VoiceTrackRegistry _tracks = null!;
    private VoiceCluster _cluster = null!;
    private ISfuClient _sfu = null!;
    private QueuedHttpMessageHandler _handler = null!;
    private CloudflareService _cf = null!;
    private VoiceCloudflareEndpoints _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _tracks = new VoiceTrackRegistry();
        _cluster = new VoiceCluster(new VoiceGridConfig());
        _sfu = Substitute.For<ISfuClient>();
        _handler = new QueuedHttpMessageHandler();
        _cf = new CloudflareService(new FakeHttpClientFactory(_handler), NullLogger<CloudflareService>.Instance);
        _endpoint = new VoiceCloudflareEndpoints();

        // Every CF action must act as a session the caller minted; CreateSession records this, and
        // these tests call the other endpoints directly. Without it a player could drive
        // renegotiation on, publish into, or tear down another player's session using an id that
        // proximity voice hands out to anyone who comes into earshot.
        _tracks.RegisterSession(UserId, "cf-session");
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    private static CfTrackNew LocalAudioTrack => new("local", Mid: "0", TrackName: "audio");
    private static CfSessionDescription Sdp => new("answer", "v=0 sdp-body");

    /// <summary>
    /// Makes <paramref name="sessionId"/> a pullable peer of the caller: registers its owner and
    /// places both players in the same voice cell. Subscribing to a remote track now requires the
    /// peer to be currently audible - proximity used to be enforced only by the client honouring
    /// isle.PeerLeft, so one brief encounter bought indefinite listening from anywhere on the map.
    /// </summary>
    private void SeedAudiblePeer(string peerId, string sessionId)
    {
        _tracks.RegisterSession(peerId, sessionId);
        _cluster.MovePlayer(UserId, 0, 0, 0);
        _cluster.MovePlayer(peerId, 1, 1, 0);
    }

    // ── CreateSession ─────────────────────────────────────────────────────

    [Test]
    public async Task CreateSession_NoUserId_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateSession(TestPrincipal.CreateAnonymous(), _cf, _registry, _tracks, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateSession_NotYetJoinedVoice_ReturnsBadRequest()
    {
        var result = await _endpoint.CreateSession(TestPrincipal.Create(UserId), _cf, _registry, _tracks, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateSession_JoinedVoice_ReturnsCloudflareSessionId()
    {
        await _registry.RegisterAsync(UserId, "steam-1");
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"sessionId":"cf-session-123"}""");

        var result = await _endpoint.CreateSession(TestPrincipal.Create(UserId), _cf, _registry, _tracks, CancellationToken.None);

        var value = ((IValueHttpResult)result).Value!;
        Assert.That(GetProp<string>(value, "cfSessionId"), Is.EqualTo("cf-session-123"));
    }

    // ── TracksNew ─────────────────────────────────────────────────────────

    [Test]
    public async Task TracksNew_NoUserId_ReturnsUnauthorized()
    {
        var body = new IsleTracksNewBody("cf-session", Sdp, [LocalAudioTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.CreateAnonymous(), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task TracksNew_ActingAsSomeoneElsesSession_IsForbidden()
    {
        // A Cloudflare session id is a bearer capability over the whole CF app, and proximity voice
        // hands peers each other's ids on first earshot - so ownership has to be checked server-side.
        _tracks.RegisterSession("someone-else", "victim-session");
        var body = new IsleTracksNewBody("victim-session", Sdp, [LocalAudioTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_handler.Requests, Is.Empty, "nothing may reach Cloudflare on a foreign session");
        });
    }

    [Test]
    public async Task TracksNew_SubscribingToAPeerWhoIsNoLongerAudible_IsForbidden()
    {
        // Proximity is the entire access-control boundary of positional voice, and it used to be
        // enforced only by the client honouring isle.PeerLeft: the peer's session id is disclosed on
        // first earshot and never rotated, so one encounter bought the ability to keep listening
        // from anywhere on the map, indefinitely.
        _tracks.RegisterSession("far-away-peer", "far-session");
        _cluster.MovePlayer(UserId, 0, 0, 0);
        _cluster.MovePlayer("far-away-peer", 100_000, 100_000, 0);

        var body = new IsleTracksNewBody(
            "cf-session", Sdp, [new CfTrackNew("remote", SessionId: "far-session", TrackName: "audio")]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_handler.Requests, Is.Empty, "an out-of-earshot pull must never reach Cloudflare");
        });
    }

    [Test]
    public async Task TracksNew_NoLocalAudioTrack_DoesNotPublishOrSubscribeButStillReturnsCloudflareResult()
    {
        var remoteTrack = new CfTrackNew("remote", SessionId: "peer-session", TrackName: "audio");
        SeedAudiblePeer("peer-1", "peer-session");
        QueueTracksNewResponse();
        var body = new IsleTracksNewBody("cf-session", Sdp, [remoteTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Ok<CfTracksNewResponse>>());
        Assert.That(_tracks.TryGet(UserId, out _), Is.False);
        await _sfu.DidNotReceiveWithAnyArgs().SubscribeMutual(default!, default!);
        await _sfu.DidNotReceiveWithAnyArgs().SendSelfPosition(default!, default, default, default, default, default, default, default, default);
    }

    [Test]
    public async Task TracksNew_LocalAudioTrackButNoOneElseInCluster_PublishesTrackAndSkipsSelfPosition()
    {
        QueueTracksNewResponse();
        var body = new IsleTracksNewBody("cf-session", Sdp, [LocalAudioTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Ok<CfTracksNewResponse>>());
        Assert.That(_tracks.TryGet(UserId, out var track), Is.True);
        Assert.That(track.CfSessionId, Is.EqualTo("cf-session"));
        Assert.That(track.TrackName, Is.EqualTo("audio"));
        // Not in the cluster yet (no MovePlayer call), so there is no self position to seed and no peers to subscribe.
        await _sfu.DidNotReceiveWithAnyArgs().SendSelfPosition(default!, default, default, default, default, default, default, default, default);
        await _sfu.DidNotReceiveWithAnyArgs().SubscribeMutual(default!, default!);
    }

    [Test]
    public async Task TracksNew_LocalAudioTrackWithAudiblePeer_PublishesSubscribesMutualAndSeedsBothPositions()
    {
        _cluster.MovePlayer(UserId, 0, 0, 0);
        _cluster.MovePlayer("peer-1", 50, 50, 0);
        QueueTracksNewResponse();
        var body = new IsleTracksNewBody("cf-session", Sdp, [LocalAudioTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Ok<CfTracksNewResponse>>());
        await _sfu.Received(1).SendSelfPosition(UserId,
            Arg.Is<float>(v => v == 0), Arg.Is<float>(v => v == 0), Arg.Is<float>(v => v == 0),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
        await _sfu.Received(1).SubscribeMutual(UserId, "peer-1");
        await _sfu.Received(1).SendPeerPosition(UserId, "peer-1",
            Arg.Is<float>(v => v == 50), Arg.Is<float>(v => v == 50), Arg.Is<float>(v => v == 0),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task TracksNew_CloudflareRejectsTheRequest_ThrowsAndPublishesNothing()
    {
        _handler.EnqueueJson(System.Net.HttpStatusCode.BadRequest, """{"error":"bad sdp"}""");
        var body = new IsleTracksNewBody("cf-session", Sdp, [LocalAudioTrack]);

        Assert.That(async () => await _endpoint.TracksNew(
                body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None),
            Throws.TypeOf<CloudflareCallsException>());
        Assert.That(_tracks.TryGet(UserId, out _), Is.False);
    }

    // ── Subscribe retries (per-track Cloudflare failures) ─────────────────

    private static CfTrackNew RemoteAudioTrack => new("remote", SessionId: "cf-peer-session", TrackName: "audio");

    /// A 200 whose sessionDescription is fine but whose track carries Cloudflare's per-track error
    /// fields instead of a mid - how "that track hasn't propagated to my edge yet" actually arrives.
    private const string PerTrackErrorBody = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0" },
          "tracks": [
            { "trackName": "audio", "sessionId": "cf-peer-session",
              "errorCode": "TrackNotFound", "errorDescription": "Track not found" }
          ]
        }
        """;

    private const string HealthySubscribeBody = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0 subscribed" },
          "tracks": [ { "trackName": "audio", "mid": "1", "sessionId": "cf-peer-session" } ]
        }
        """;

    [Test]
    public async Task TracksNew_SubscribeWhoseTrackPropagatesLate_SucceedsOnARetry()
    {
        // The point of the retry: VoiceSubscriptionReconcileService marks a pair as pushed once it
        // has sent SubscribeMutual, regardless of what the client made of it, so a subscribe that
        // fails here is not re-driven while both peers stay in earshot. Absorbing the propagation
        // window is what stops that becoming a stuck one-way silence.
        SeedAudiblePeer("peer-1", "cf-peer-session");
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, PerTrackErrorBody);
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, HealthySubscribeBody);
        var body = new IsleTracksNewBody("cf-session", Sdp, [RemoteAudioTrack]);

        var result = await _endpoint.TracksNew(
            body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None);

        var value = (CfTracksNewResponse)((IValueHttpResult)result).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(value.Tracks[0].Mid, Is.EqualTo("1"));
            Assert.That(_handler.Requests, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void TracksNew_SubscribeThatKeepsFailing_GivesUpAndThrowsRatherThanReportingSuccess()
    {
        SeedAudiblePeer("peer-1", "cf-peer-session");
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, PerTrackErrorBody);
        var body = new IsleTracksNewBody("cf-session", Sdp, [RemoteAudioTrack]);

        Assert.That(async () => await _endpoint.TracksNew(
                body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None),
            Throws.TypeOf<CloudflareCallsException>());
        // Against the shared schedule rather than a literal: the budget is owned by
        // CloudflareService.SubscribeRetryDelays, and it is sized to cover the publisher's whole
        // WebRTC handshake, not just Cloudflare's internal propagation.
        Assert.That(_handler.Requests,
            Has.Count.EqualTo(CloudflareService.SubscribeRetryDelays.Count + 1),
            "should exhaust the shared subscribe retry schedule before giving up");
    }

    [Test]
    public void TracksNew_PublishThatFails_IsNotRetried()
    {
        // Retrying a rejected publish only replays the same SDP, and it must still publish nothing.
        // Publish-only, so no peer audibility is involved.
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, PerTrackErrorBody);
        var body = new IsleTracksNewBody("cf-session", Sdp, [LocalAudioTrack]);

        Assert.That(async () => await _endpoint.TracksNew(
                body, TestPrincipal.Create(UserId), _cf, _tracks, _cluster, _sfu, CancellationToken.None),
            Throws.TypeOf<CloudflareCallsException>());
        Assert.Multiple(() =>
        {
            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            Assert.That(_tracks.TryGet(UserId, out _), Is.False);
        });
    }

    // ── Renegotiate ───────────────────────────────────────────────────────

    [Test]
    public async Task Renegotiate_CloudflareAccepts_ReturnsTheNewSessionDescription()
    {
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"sessionDescription":{"type":"answer","sdp":"v=0 renegotiated"}}""");
        var body = new IsleRenegotiateBody("cf-session", Sdp);

        var result = await _endpoint.Renegotiate(body, TestPrincipal.Create(UserId), _cf, _tracks, CancellationToken.None);

        var value = (CfRenegotiateResponse)((IValueHttpResult)result).Value!;
        Assert.That(value.SessionDescription.Sdp, Is.EqualTo("v=0 renegotiated"));
    }

    // ── CloseTracks ───────────────────────────────────────────────────────

    [Test]
    public async Task CloseTracks_NoUserId_ReturnsUnauthorized()
    {
        var body = new IsleCloseTracksBody("cf-session", ["audio"]);

        var result = await _endpoint.CloseTracks(body, TestPrincipal.CreateAnonymous(), _cf, _tracks, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CloseTracks_ClosingTheAudioTrack_RemovesItFromTheRegistryAndReturnsNoContent()
    {
        _tracks.Publish(UserId, "cf-session", "audio");
        _handler.Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var body = new IsleCloseTracksBody("cf-session", ["audio"]);

        var result = await _endpoint.CloseTracks(body, TestPrincipal.Create(UserId), _cf, _tracks, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_tracks.TryGet(UserId, out _), Is.False);
    }

    [Test]
    public async Task CloseTracks_ClosingANonAudioTrack_LeavesTheAudioTrackRegistered()
    {
        _tracks.Publish(UserId, "cf-session", "audio");
        _handler.Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var body = new IsleCloseTracksBody("cf-session", ["video"]);

        await _endpoint.CloseTracks(body, TestPrincipal.Create(UserId), _cf, _tracks, CancellationToken.None);

        Assert.That(_tracks.TryGet(UserId, out _), Is.True);
    }

    [Test]
    public async Task CloseTracks_CloudflareReturnsNotAcceptable_IsTreatedAsSuccessAndStillRemovesTheTrack()
    {
        // CloudflareService special-cases 406 as an already-closed/idempotent no-op rather than throwing.
        _tracks.Publish(UserId, "cf-session", "audio");
        _handler.Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.NotAcceptable));
        var body = new IsleCloseTracksBody("cf-session", ["audio"]);

        var result = await _endpoint.CloseTracks(body, TestPrincipal.Create(UserId), _cf, _tracks, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_tracks.TryGet(UserId, out _), Is.False);
    }

    private void QueueTracksNewResponse() =>
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK,
            """{"sessionDescription":{"type":"answer","sdp":"v=0 answer"},"tracks":[{"mid":"0","trackName":"audio","location":"local"}],"requiresImmediateRenegotiation":false}""");

    /// <summary>Reads a property off the anonymous type CreateSession returns via Results.Ok(new
    /// { ... }) - avoids adding a `dynamic`/Microsoft.CSharp dependency just for tests. Same
    /// convention as GuildTemplateEndpointTests.GetProp.</summary>
    private static T GetProp<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;
}
