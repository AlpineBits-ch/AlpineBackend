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

/// <summary>Pins the defect behind "No audio received from this participant".</summary>
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

        // Every CF action must act as a session the caller minted - CreateSession records this, and
        // these tests call TracksNew directly.
        _cache.SetEntry("guild-cf-session-owner:cf-publisher", PublisherId);
    }

    /// <summary>
    /// The channel as it looks the instant the publisher's <c>tracks/new</c> lands:
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
        // Precondition for everything below: CfSessionId alone carries no information about whether
        // the participant is publishing.
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
        // Cloudflare has nothing behind.
        Assert.That(ParticipantJoinedPayloads().Where(p => p.Contains(MidJoinerId)), Is.Empty,
            "a participant with no AudioTrackName has published nothing and must not be announced "
            + "as pullable; they announce themselves when their own tracks/new lands");
    }

    [Test]
    public async Task PublishingAudio_DoesNotFabricateAnAudioTrackNameForANonPublisher()
    {
        SeedChannelState();

        await PublishAudioAsync();

        // `p.AudioTrackName ??
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
