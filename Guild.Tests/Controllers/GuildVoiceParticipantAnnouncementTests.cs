using Echo.Voice.Transport;
using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using System.Text.Json;
using Echo.Realtime.Caching;
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
    private GuildVoiceMediaController _controller = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _voiceStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        var permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _controller = new GuildVoiceMediaController(
            new FakeVoiceSfu(), permissions,
            NullLogger<GuildVoiceMediaController>.Instance, _cache,
            VoiceTestHarness.ServiceFor(_cache, new FakeDistributedLockService(), _hub))
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
    /// </summary>
    private void SeedChannelState()
    {
        var state = new VoiceRoom
        {
            RoomId = ChannelId, Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants =
            [
                new VoiceParticipant { UserId = PublisherId },
                new VoiceParticipant
                {
                    UserId = MidJoinerId, MediaSessionId = "cf-midjoiner", AudioTrackName = null,
                },
                new VoiceParticipant
                {
                    UserId = EstablishedId, MediaSessionId = "cf-established", AudioTrackName = "audio",
                },
            ],
        };
        _cache.SetEntry(VoiceRoomKey.Channel(ChannelId).CacheKey, JsonSerializer.Serialize(state));
    }

    private Task<IActionResult> PublishAudioAsync() => _controller.Publish(
        GuildId, ChannelId, new GuildPublishBody(["audio"]), CancellationToken.None);

    /// <summary>Every ParticipantJoined payload emitted, as JSON (the payloads are anonymous types
    /// declared in another assembly, so serialising is the only way to inspect them).</summary>
    private List<string> ParticipantJoinedPayloads() =>
        ((FakeHubClients)_hub.Clients).SentMessages
            .Where(m => m.Method == "guild.voice.ParticipantJoined")
            .Select(m => JsonSerializer.Serialize(m.Args[0]))
            .ToList();

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreatingAConnection_LeavesTheParticipantUnpublished()
    {
        // The original defect started here.
        _cache.SetEntry(VoiceRoomKey.Channel(ChannelId).CacheKey, JsonSerializer.Serialize(
            new VoiceRoom
            {
                RoomId = ChannelId, Kind = VoiceRoomKind.Channel, GuildId = GuildId,
                Participants = [new VoiceParticipant { UserId = PublisherId }],
            }));

        await _controller.CreateConnection(GuildId, ChannelId, CancellationToken.None);

        var me = (await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId)))!
            .Participants.Single();
        Assert.Multiple(() =>
        {
            Assert.That(me.PublishState, Is.EqualTo(VoicePublishState.Joined));
            Assert.That(me.MediaSessionId, Is.Null,
                "no audio track has been published yet - the client is still acquiring a microphone");
            Assert.That(me.AudioTrackName, Is.Null);
        });
    }

    [Test]
    public async Task PublishingAudio_DoesNotAnnounceAParticipantWhoHasNotPublishedYet()
    {
        SeedChannelState();

        await PublishAudioAsync();

        // Announcing the mid-joiner here hands the publisher a (session, trackName) pair the SFU
        // has nothing behind.
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
    public async Task PublishingAudio_StillTellsThePublisherWhoElseIsPullable()
    {
        // The behaviour the fix must preserve: an established participant still reaches the
        // publisher, otherwise nobody would ever hear anyone.
        SeedChannelState();

        await PublishAudioAsync();

        var snapshot = ((FakeHubClients)_hub.Clients).SentMessages
            .Where(m => m.Method == "guild.voice.Snapshot")
            .Select(m => (VoiceRoomSnapshot)m.Args[0]!)
            .Last();

        var established = snapshot.Participants.Single(p => p.UserId == EstablishedId);
        Assert.Multiple(() =>
        {
            Assert.That(established.PublishState, Is.EqualTo(nameof(VoicePublishState.Publishing)));
            Assert.That(established.MediaSessionId, Is.EqualTo("cf-established"));
            Assert.That(snapshot.Participants.Single(p => p.UserId == MidJoinerId).PublishState,
                Is.EqualTo(nameof(VoicePublishState.Joined)),
                "and the mid-joiner is still reported as not yet pullable");
        });
    }
}
