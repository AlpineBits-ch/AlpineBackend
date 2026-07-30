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
/// Covers VoiceCloudflareEndpoints - the WebRTC signalling relay for Isle proximity voice.
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
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    private static CfTrackNew LocalAudioTrack => new("local", Mid: "0", TrackName: "audio");
    private static CfSessionDescription Sdp => new("answer", "v=0 sdp-body");

    // ── CreateSession ─────────────────────────────────────────────────────

    [Test]
    public async Task CreateSession_NoUserId_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateSession(TestPrincipal.CreateAnonymous(), _cf, _registry, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateSession_NotYetJoinedVoice_ReturnsBadRequest()
    {
        var result = await _endpoint.CreateSession(TestPrincipal.Create(UserId), _cf, _registry, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateSession_JoinedVoice_ReturnsCloudflareSessionId()
    {
        await _registry.RegisterAsync(UserId, "steam-1");
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"sessionId":"cf-session-123"}""");

        var result = await _endpoint.CreateSession(TestPrincipal.Create(UserId), _cf, _registry, CancellationToken.None);

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
    public async Task TracksNew_NoLocalAudioTrack_DoesNotPublishOrSubscribeButStillReturnsCloudflareResult()
    {
        var remoteTrack = new CfTrackNew("remote", SessionId: "peer-session", TrackName: "audio");
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

    // ── Renegotiate ───────────────────────────────────────────────────────

    [Test]
    public async Task Renegotiate_CloudflareAccepts_ReturnsTheNewSessionDescription()
    {
        _handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"sessionDescription":{"type":"answer","sdp":"v=0 renegotiated"}}""");
        var body = new IsleRenegotiateBody("cf-session", Sdp);

        var result = await _endpoint.Renegotiate(body, _cf, CancellationToken.None);

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
