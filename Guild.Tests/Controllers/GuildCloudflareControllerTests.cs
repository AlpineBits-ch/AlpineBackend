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

/// <summary>
/// Covers GuildCloudflareController.CreateSession's <c>primary</c> flag.
///
/// A participant's ChannelVoiceState entry holds exactly one CfSessionId, and new joiners use it to
/// find that participant's <em>audio</em>. A desktop client that publishes its screen from a
/// separate process opens a second Cloudflare session; if that session were recorded against the
/// participant, everyone joining afterwards would subscribe to a session carrying no audio and
/// silently hear nothing. These tests pin that boundary.
/// </summary>
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
            StubCloudflareHttp.CreateService(), permissions, new FakeHubContext(),
            NullLogger<GuildCloudflareController>.Instance, _cache, _voiceStore, _context)
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
        var state = new ChannelVoiceState
        {
            ChannelId = ChannelId,
            GuildId = GuildId,
            Participants =
            [
                new VoiceState
                {
                    UserId = UserId, ChannelId = ChannelId, GuildId = GuildId,
                    CfSessionId = ExistingSessionId, AudioTrackName = "audio",
                },
            ],
        };
        await _cache.SetStringAsync(
            ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(state));
    }

    private async Task<string?> StoredSessionIdAsync()
    {
        var raw = await _cache.GetStringAsync(ChannelVoiceState.GetCacheKey(ChannelId));
        var state = JsonSerializer.Deserialize<ChannelVoiceState>(raw!);
        return state!.Participants.Single(p => p.UserId == UserId).CfSessionId;
    }

    [Test]
    public async Task CreateSession_DefaultsToPrimary_AndRecordsTheSessionOnTheParticipant()
    {
        // Existing callers pass no flag at all; their behaviour must be unchanged.
        var result = await _controller.CreateSession(GuildId, ChannelId, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(await StoredSessionIdAsync(), Is.EqualTo(StubCloudflareHttp.SessionId));
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
        // TrackPublished carries to subscribers. Serialised rather than reflected over, because
        // the response is an anonymous type declared in another assembly.
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
