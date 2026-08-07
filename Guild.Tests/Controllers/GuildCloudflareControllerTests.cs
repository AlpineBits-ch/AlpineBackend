using Echo.Realtime.Sfu;
using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
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

/// <summary>Covers GuildCloudflareController.CreateSession's <c>primary</c> flag.</summary>
[TestFixture]
public class GuildCloudflareControllerTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string UserId = "user-1";
    private const string OwnerId = "owner-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";
    private const string ExistingSessionId = "cf-session-primary";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private LockedJsonCacheStore _voiceStore = null!;
    private GuildCloudflareController _controller = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _voiceStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        var permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _controller = new GuildCloudflareController(
            StubCloudflareHttp.CreateService(), permissions,
            NullLogger<GuildCloudflareController>.Instance, _cache,
            VoiceTestHarness.ServiceFor(_cache, new FakeDistributedLockService(), new FakeHubContext()),
            new SfuSessionOwnership(_cache))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(UserId) },
            },
        };

        await SeedMemberWithConnectPermission();
        await SeedVoiceStateWithExistingSession();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedMemberWithConnectPermission()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        // The Connect check is channel-scoped, so the channel has to exist for it to resolve.
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "c", Description = "d",
            Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "r", Permissions = Permissions.Connect,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    /// The user is already in the channel with a primary session, as they would be before sharing.
    private async Task SeedVoiceStateWithExistingSession()
    {
        var state = new VoiceRoom
        {
            RoomId = ChannelId, Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants =
            [
                new VoiceParticipant
                {
                    UserId = UserId, CfSessionId = ExistingSessionId, AudioTrackName = "audio",
                },
            ],
        };
        await _cache.SetStringAsync(
            VoiceRoomKey.Channel(ChannelId).CacheKey, JsonSerializer.Serialize(state));
    }

    /// <summary>Adds Speak to the member's role.</summary>
    private async Task GrantSpeakAsync()
    {
        var role = _context.Roles.Single(r => r.Id == RoleId);
        role.Permissions = Permissions.Connect | Permissions.Speak;
        await _context.SaveChangesAsync();
        _cache.Remove($"perms:{UserId}:{ChannelId}");
    }

    private async Task<string?> StoredSessionIdAsync() => (await ParticipantAsync())?.CfSessionId;

    private async Task<VoiceParticipant?> ParticipantAsync()
    {
        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        return room?.Participants.SingleOrDefault(p => p.UserId == UserId);
    }

    /// <summary>Replaces the fixture's already-publishing seed with a participant who has merely
    /// joined - the state the two tests below are about.</summary>
    private Task SeedJoinedButNotPublishingAsync() =>
        _cache.SetStringAsync(VoiceRoomKey.Channel(ChannelId).CacheKey, JsonSerializer.Serialize(
            new VoiceRoom
            {
                RoomId = ChannelId, Kind = VoiceRoomKind.Channel, GuildId = GuildId,
                Participants = [new VoiceParticipant { UserId = UserId }],
            }));

    [Test]
    public async Task CreateSession_DoesNotMarkTheParticipantAsPublishing()
    {
        await SeedJoinedButNotPublishingAsync();

        // Existing callers pass no flag at all; they still get a session back.
        var result = await _controller.CreateSession(GuildId, ChannelId, CancellationToken.None);
        Assert.That(result, Is.InstanceOf<OkObjectResult>());

        // But opening a session is not publishing, and the roster no longer pretends otherwise.
        var me = await ParticipantAsync();
        Assert.Multiple(() =>
        {
            Assert.That(me!.PublishState, Is.EqualTo(VoicePublishState.Joined));
            Assert.That(me.CfSessionId, Is.Null);
            Assert.That(me.AudioTrackName, Is.Null);
        });
    }

    [Test]
    public async Task PublishingAudio_IsWhatMakesTheParticipantPullable()
    {
        await SeedJoinedButNotPublishingAsync();

        // Publishing needs Speak on top of Connect, and the session acted as must be the caller's.
        await GrantSpeakAsync();
        _cache.SetEntry("voice:session-owner:cf-owned-session", UserId);

        var published = await _controller.TracksNew(GuildId, ChannelId, new GuildTracksNewBody(
            "cf-owned-session",
            new CfSessionDescription("offer", "v=0"),
            [new CfTrackNew("local", Mid: "0", TrackName: "audio")]), CancellationToken.None);
        Assert.That(published, Is.InstanceOf<OkObjectResult>());

        var me = await ParticipantAsync();
        Assert.Multiple(() =>
        {
            Assert.That(me!.PublishState, Is.EqualTo(VoicePublishState.Publishing));
            Assert.That(me.CfSessionId, Is.EqualTo("cf-owned-session"));
            Assert.That(me.AudioTrackName, Is.EqualTo("audio"));
        });
    }

    [Test]
    public async Task CreateSession_Secondary_LeavesTheParticipantsPrimarySessionIntact()
    {
        await _controller.CreateSession(GuildId, ChannelId, CancellationToken.None, primary: false);

        // The screen session must not displace the audio session, or new joiners subscribe to a
        // session with no audio on it and hear nothing.
        Assert.That(await StoredSessionIdAsync(), Is.EqualTo(ExistingSessionId));
    }

    [Test]
    public async Task CreateSession_Secondary_StillReturnsTheNewSessionId()
    {
        var result = await _controller.CreateSession(
            GuildId, ChannelId, CancellationToken.None, primary: false);

        // The caller needs it: it is what they publish the screen track against, and what
        // TrackPublished carries to subscribers.
        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.That(payload, Does.Contain(StubCloudflareHttp.SessionId));
    }

    [Test]
    public async Task CreateSession_Secondary_DoesNotRewriteTheUsersVoiceLocation()
    {
        await _cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(UserId));

        await _controller.CreateSession(GuildId, ChannelId, CancellationToken.None, primary: false);

        // Location tracking belongs to the primary session; a screen share does not move the user.
        var location = await _cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(UserId));
        Assert.That(location, Is.Null);
    }

    [Test]
    public async Task CreateSession_WithoutConnectPermission_IsStillForbiddenForSecondarySessions()
    {
        // The permission gate must not be bypassable by asking for a secondary session.
        _controller.ControllerContext.HttpContext.User = TestPrincipal.Create("outsider");

        var result = await _controller.CreateSession(
            GuildId, ChannelId, CancellationToken.None, primary: false);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }
}
