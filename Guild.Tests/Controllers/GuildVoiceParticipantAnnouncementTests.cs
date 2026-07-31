using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Sfu;
using Guild.Application.Controllers;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>
/// Pins the defect behind "No audio received from this participant".
///
/// <para>Guild voice has exactly one way for a client to learn which Cloudflare session and track
/// name to pull another participant's audio from: the <c>guild.voice.ParticipantJoined</c> events
/// that <c>GuildCloudflareController.ExchangeParticipantJoined</c> emits. There is deliberately no
/// HTTP fallback - <see cref="VoiceStateResponse"/> omits <c>cfSessionId</c>/<c>audioTrackName</c>
/// ("so clients discover them only via ParticipantJoined events"). So an event that names a track
/// that does not exist on Cloudflare is not a recoverable hiccup: it is terminal.</para>
///
/// <para>It is terminal because every client dedupes subscriptions per user - Alpine's
/// <c>subscribedAudioUserIds</c>, mobile's <c>_subscribedKeys</c>. The first (bad) announcement
/// consumes the guard; when the participant really does publish moments later and a correct
/// ParticipantJoined arrives, it is discarded as a duplicate. One-way silence for the rest of the
/// session.</para>
///
/// <para>The bad announcement is produced by treating <c>CfSessionId is not null</c> as "this
/// participant is publishing audio". It is not: <c>CreateSession(primary: true)</c> writes
/// <c>CfSessionId</c> at session-creation time, and the audio track is only published later, by a
/// separate <c>tracks/new</c> call. The window between the two is whatever the client needs to
/// acquire a microphone - near-zero on Alpine (which calls <c>getUserMedia</c> before
/// <c>createSession</c>) and seconds on mobile, whose <c>GuildVoiceWebRtcService.connect</c> calls
/// <c>cfCreateSession</c> first and only then hits the OS mic-permission prompt. That asymmetry is
/// why the symptom is reported far more often against mobile participants.</para>
/// </summary>
[TestFixture]
public class GuildVoiceParticipantAnnouncementTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string PublisherId = "publisher-1";
    private const string MidJoinerId = "midjoiner-1";
    private const string EstablishedId = "established-1";
    private const string OwnerId = "owner-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private LockedJsonCacheStore _voiceStore = null!;
    private GuildCloudflareController _controller = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _voiceStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        var permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _controller = new GuildCloudflareController(
            StubCloudflareHttp.CreateService(), permissions, _hub,
            NullLogger<GuildCloudflareController>.Instance, _cache, _voiceStore, _context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(PublisherId) },
            },
        };

        await SeedGuildAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "General", Description = "d",
            Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-1", GuildId = GuildId, Name = "member",
            Permissions = Permissions.Connect | Permissions.Speak,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = PublisherId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{PublisherId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = "role-1", MemberId = "member-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// The channel as it looks the instant the publisher's <c>tracks/new</c> lands:
    /// <list type="bullet">
    /// <item><b>mid-joiner</b> - has called <c>POST /session</c> (so CfSessionId is recorded) but is
    /// still waiting on the OS microphone prompt, so no track exists on Cloudflare yet.</item>
    /// <item><b>established</b> - already published; their track really is pullable.</item>
    /// </list>
    /// </summary>
    private void SeedChannelState()
    {
        var state = new ChannelVoiceState
        {
            ChannelId = ChannelId,
            GuildId = GuildId,
            Participants =
            [
                new VoiceState { UserId = PublisherId, ChannelId = ChannelId, GuildId = GuildId },
                new VoiceState
                {
                    UserId = MidJoinerId, ChannelId = ChannelId, GuildId = GuildId,
                    CfSessionId = "cf-midjoiner", AudioTrackName = null,
                },
                new VoiceState
                {
                    UserId = EstablishedId, ChannelId = ChannelId, GuildId = GuildId,
                    CfSessionId = "cf-established", AudioTrackName = "audio",
                },
            ],
        };
        _cache.SetEntry(ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(state));
    }

    private Task<IActionResult> PublishAudioAsync() => _controller.TracksNew(
        GuildId, ChannelId,
        new GuildTracksNewBody(
            "cf-publisher",
            new CfSessionDescription("offer", "v=0"),
            [new CfTrackNew("local", Mid: "0", TrackName: "audio")]),
        CancellationToken.None);

    /// <summary>Every ParticipantJoined payload emitted, as JSON (the payloads are anonymous types
    /// declared in another assembly, so serialising is the only way to inspect them - same approach
    /// as <see cref="GuildCloudflareControllerTests"/>).</summary>
    private List<string> ParticipantJoinedPayloads() =>
        ((FakeHubClients)_hub.Clients).SentMessages
            .Where(m => m.Method == "guild.voice.ParticipantJoined")
            .Select(m => JsonSerializer.Serialize(m.Args[0]))
            .ToList();

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateSession_RecordsACfSessionIdBeforeAnyAudioTrackExists()
    {
        // Precondition for everything below: CfSessionId alone carries no information about
        // whether the participant is publishing. This passes today - it documents why the
        // `CfSessionId is not null` test in ExchangeParticipantJoined is the wrong question.
        _cache.SetEntry(ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(
            new ChannelVoiceState
            {
                ChannelId = ChannelId, GuildId = GuildId,
                Participants = [new VoiceState { UserId = PublisherId, ChannelId = ChannelId, GuildId = GuildId }],
            }));

        await _controller.CreateSession(GuildId, ChannelId, CancellationToken.None);

        var raw = await _cache.GetStringAsync(ChannelVoiceState.GetCacheKey(ChannelId));
        var me = JsonSerializer.Deserialize<ChannelVoiceState>(raw!)!.Participants.Single();
        Assert.Multiple(() =>
        {
            Assert.That(me.CfSessionId, Is.EqualTo(StubCloudflareHttp.SessionId));
            Assert.That(me.AudioTrackName, Is.Null,
                "no audio track has been published yet - the client is still acquiring a microphone");
        });
    }

    [Test]
    public async Task PublishingAudio_DoesNotAnnounceAParticipantWhoHasNotPublishedYet()
    {
        SeedChannelState();

        await PublishAudioAsync();

        // Announcing the mid-joiner here hands the publisher a (session, trackName) pair that
        // Cloudflare has nothing behind. The subscribe fails, the per-user dedupe guard is spent,
        // and the mid-joiner's real ParticipantJoined - moments later - is dropped as a duplicate.
        Assert.That(ParticipantJoinedPayloads().Where(p => p.Contains(MidJoinerId)), Is.Empty,
            "a participant with no AudioTrackName has published nothing and must not be announced "
            + "as pullable; they announce themselves when their own tracks/new lands");
    }

    [Test]
    public async Task PublishingAudio_DoesNotFabricateAnAudioTrackNameForANonPublisher()
    {
        SeedChannelState();

        await PublishAudioAsync();

        // `p.AudioTrackName ?? "audio"` turns "this participant has published nothing" into a
        // confident claim that they have. Without the fallback the bug would at least be visible.
        // Null when nothing was announced for them at all, which is the outcome we want.
        var midJoiner = ParticipantJoinedPayloads().FirstOrDefault(p => p.Contains(MidJoinerId)) ?? "";
        Assert.That(midJoiner, Does.Not.Contain("\"audioTrackName\":\"audio\""),
            $"payload fabricated a track name for a participant that has none: {midJoiner}");
    }

    [Test]
    public async Task PublishingAudio_StillAnnouncesParticipantsWhoHaveActuallyPublished()
    {
        // The behaviour the fix must preserve: an established participant is still replayed to the
        // joiner, otherwise nobody would ever hear anyone.
        SeedChannelState();

        await PublishAudioAsync();

        Assert.That(ParticipantJoinedPayloads().Any(p => p.Contains(EstablishedId)), Is.True);
    }
}
