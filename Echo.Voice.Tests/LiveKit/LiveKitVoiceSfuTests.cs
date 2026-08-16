using System.Net;
using System.Text.Json;
using Echo.Realtime.LiveKit;
using Echo.Voice.Rooms;
using Echo.Voice.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.LiveKit;

/// <summary>
/// The translation layer: our neutral contracts in, LiveKit's control plane and token out.
/// </summary>
[TestFixture]
public class LiveKitVoiceSfuTests
{
    private static readonly LiveKitOptions Options = new()
    {
        ApiKey = "APItestkey",
        ApiSecret = "a-secret-that-is-long-enough-to-be-a-key",
        Nodes = [new LiveKitNode("fsn1", "wss://sfu-fsn1.venta.gg", "http://10.10.0.2:7880")],
    };

    private RecordingHandler _handler = null!;
    private LiveKitVoiceSfu _sfu = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new RecordingHandler();
        var client = new LiveKitRoomClient(
            new SingleClientFactory(_handler), Options, NullLogger<LiveKitRoomClient>.Instance);

        // No Redis: the registry then falls back to the fleet's only node, which is the shipped
        // single-node state and the one this suite is about.
        var registry = new LiveKitRoomRegistry(Options, NullLogger<LiveKitRoomRegistry>.Instance);

        _sfu = new LiveKitVoiceSfu(client, registry, Options, NullLogger<LiveKitVoiceSfu>.Instance);
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    // ── Naming ────────────────────────────────────────────────────────────────

    /// <summary>Channel ids and call ids come from different id spaces and there is one room
    /// namespace on the fleet, so an unprefixed name could put a DM call and a guild channel in the
    /// same room - which is a privacy incident rather than a bug.</summary>
    [Test]
    public void Room_names_are_prefixed_by_kind_so_two_id_spaces_cannot_collide()
    {
        Assert.That(
            LiveKitVoiceSfu.RoomName(VoiceRoomKey.Channel("42")),
            Is.Not.EqualTo(LiveKitVoiceSfu.RoomName(VoiceRoomKey.Call("42"))));
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Connecting_creates_the_room_before_it_mints_a_token()
    {
        var connection = await _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full);

        Assert.Multiple(() =>
        {
            Assert.That(_handler.Paths, Is.EqualTo(new[] { "/twirp/livekit.RoomService/CreateRoom" }),
                "auto-create is off, so a token for a room nobody created authorises joining nothing");
            Assert.That(connection.Room, Is.EqualTo("channel-channel-1"));
            Assert.That(connection.Token, Is.Not.Empty);
        });
    }

    [Test]
    public async Task The_client_is_handed_the_public_signalling_url_and_never_the_control_one()
    {
        var connection = await _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full);

        Assert.Multiple(() =>
        {
            Assert.That(connection.Url, Is.EqualTo("wss://sfu-fsn1.venta.gg"));
            Assert.That(connection.Url, Does.Not.Contain("10.10.0.2"),
                "the control address is on the overlay - a browser cannot resolve it, so handing it "
                + "over is a connection that can only ever fail");
        });
    }

    /// <summary>
    /// The whole point of the rights type reaching this far: a member whose plan has no video left
    /// must be unable to turn a camera on however their client is patched, and that is a source list
    /// in the token rather than a number on a response body.
    /// </summary>
    [Test]
    public async Task An_audio_only_participant_is_granted_the_microphone_source_and_no_other()
    {
        var connection = await _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.AudioOnly);

        var video = PayloadOf(connection.Token).GetProperty("video");
        var sources = video.GetProperty("canPublishSources")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(video.GetProperty("canPublish").GetBoolean(), Is.True, "they can still speak");
            Assert.That(sources, Is.EqualTo(new[] { LiveKitSources.Microphone }));
        });
    }

    [Test]
    public async Task A_listener_may_neither_publish_nor_use_the_data_channel()
    {
        var connection = await _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Listener);

        var video = PayloadOf(connection.Token).GetProperty("video");

        Assert.Multiple(() =>
        {
            Assert.That(video.GetProperty("canPublish").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("canSubscribe").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("canPublishData").GetBoolean(), Is.False,
                "otherwise somebody denied Speak can still address the room");
        });
    }

    [Test]
    public async Task Every_control_call_carries_a_freshly_minted_admin_bearer()
    {
        await _sfu.ConnectAsync(VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full);

        var authorization = _handler.Requests.Single().Headers.Authorization;

        Assert.Multiple(() =>
        {
            Assert.That(authorization?.Scheme, Is.EqualTo("Bearer"));
            Assert.That(
                PayloadOf(authorization!.Parameter!).GetProperty("video")
                    .GetProperty("roomCreate").GetBoolean(),
                Is.True);
        });
    }

    // ── Failure translation ───────────────────────────────────────────────────

    /// <summary>The distinction that matters.</summary>
    [Test]
    public void An_unreachable_control_plane_is_reported_as_unavailable()
    {
        _handler.Throw = new HttpRequestException("no route to host");

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() => _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.Unavailable));
    }

    [Test]
    public void A_five_hundred_from_the_node_is_also_unavailable()
    {
        _handler.Status = HttpStatusCode.InternalServerError;

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() => _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.Unavailable));
    }

    [Test]
    public void A_rejected_request_is_not_retryable()
    {
        _handler.Status = HttpStatusCode.BadRequest;

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() => _sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full));

        Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.Rejected),
            "a malformed room name fails the same way every time");
    }

    /// <summary>An instance with no fleet configured is a supported state - a self-hoster who does not
    /// run voice - and must be distinguishable from an outage, or the operator goes looking for one.</summary>
    [Test]
    public void An_unconfigured_instance_says_so_rather_than_failing_obscurely()
    {
        var options = new LiveKitOptions();
        var sfu = new LiveKitVoiceSfu(
            new LiveKitRoomClient(
                new SingleClientFactory(_handler), options, NullLogger<LiveKitRoomClient>.Instance),
            new LiveKitRoomRegistry(options, NullLogger<LiveKitRoomRegistry>.Instance),
            options, NullLogger<LiveKitVoiceSfu>.Instance);

        var ex = Assert.ThrowsAsync<VoiceMediaException>(() => sfu.ConnectAsync(
            VoiceRoomKey.Channel("channel-1"), "user-1", null, VoiceMediaRights.Full));

        Assert.Multiple(() =>
        {
            Assert.That(sfu.IsConfigured, Is.False);
            Assert.That(ex!.Failure, Is.EqualTo(VoiceMediaFailure.NotConfigured));
        });
    }

    // ── Reading the room back ─────────────────────────────────────────────────

    [Test]
    public async Task Listing_participants_resolves_the_user_behind_a_secondary_identity()
    {
        _handler.Body = """
            {"participants":[
              {"sid":"PA_1","identity":"user-1","name":null,"state":"ACTIVE",
               "tracks":[{"sid":"TR_1","name":"audio","source":"microphone","muted":false}]},
              {"sid":"PA_2","identity":"user-1#screen","name":null,"state":"ACTIVE",
               "tracks":[{"sid":"TR_2","name":"screen-a","source":"screen_share","muted":false}]},
              {"sid":"PA_3","identity":"user-2","name":null,"state":"DISCONNECTED","tracks":[]}
            ]}
            """;

        var participants = await _sfu.ListParticipantsAsync(VoiceRoomKey.Channel("channel-1"));

        Assert.Multiple(() =>
        {
            Assert.That(participants.Select(p => p.UserId), Is.EqualTo(new[] { "user-1", "user-1" }),
                "a screen connection belongs to the same person as the microphone one");
            Assert.That(participants[0].IsPublishing, Is.True);
            Assert.That(participants[1].IsPublishing, Is.False,
                "a share is not a microphone, and only the microphone makes somebody audible");
            Assert.That(participants.Any(p => p.UserId == "user-2"), Is.False,
                "a participant the SFU has already marked disconnected is on their way out and is "
                + "not evidence of anything");
        });
    }

    private static JsonElement PayloadOf(string token)
    {
        var segment = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(
            segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=')));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Paths => Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw is not null) throw Throw;

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
