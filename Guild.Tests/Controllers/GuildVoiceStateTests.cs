using System.Text.Json;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Guild.Application.Controllers;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary><c>GET /api/v1/voice/state</c>: the launch read behind the reconnect banner.</summary>
[TestFixture]
public class GuildVoiceStateTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-visible";
    private const string HiddenChannelId = "channel-hidden";
    private const string UserId = "user-1";
    private const string OwnerId = "user-owner";
    private const string DeviceId = "device-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        await SeedGuildAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>One guild, two voice channels, one member.</summary>
    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = GuildId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var channelId in new[] { ChannelId, HiddenChannelId })
        {
            _context.Channels.Add(new Channel
            {
                Id = channelId, GuildId = GuildId, Name = channelId, Description = "d",
                Type = ChannelType.Voice,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        _context.Roles.Add(new Role
        {
            Id = "role-1", GuildId = GuildId, Name = "member",
            Permissions = Permissions.ViewChannel | Permissions.Connect | Permissions.Speak,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = "role-1", MemberId = "member-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Denied on the hidden channel only.
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "cp-1", ChannelId = HiddenChannelId, RoleId = "role-1",
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel | Permissions.Connect,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private GuildVoiceStateController ControllerFor(string userId) =>
        new(_cache,
            VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService()),
            new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance),
            _context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(userId) },
            },
        };

    private Task<IActionResult> GetStateAsync(string userId = UserId) =>
        ControllerFor(userId).GetVoiceState(CancellationToken.None);

    /// <summary>Points the user's voice-location key at a channel, the way a join does.</summary>
    private void SeedPointer(string channelId = ChannelId) =>
        _cache.SetEntry(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new { ChannelId = channelId, GuildId, DeviceId }));

    private Task SeedRosterAsync(string channelId, params string[] userIds) =>
        VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = channelId,
            Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants = userIds
                .Select(id => new VoiceParticipant { UserId = id, DeviceId = DeviceId })
                .ToList(),
        });

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task State_WithASeatStillOnTheRoster_ReportsIt()
    {
        SeedPointer();
        await SeedRosterAsync(ChannelId, UserId);

        var result = await GetStateAsync();

        var payload = (result as OkObjectResult)?.Value as VoiceStateDto;
        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(payload!.GuildId, Is.EqualTo(GuildId));
            Assert.That(payload.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(payload.ChannelName, Is.EqualTo(ChannelId));
            Assert.That(payload.DeviceId, Is.EqualTo(DeviceId));
        });
    }

    [Test]
    public async Task State_WithNoPointerAtAll_IsNoContent()
    {
        var result = await GetStateAsync();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task State_WithAPointerAtARoomThatIsGone_IsNoContent()
    {
        // The pointer outlives the room by four hours.
        SeedPointer();

        var result = await GetStateAsync();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task State_WithAPointerAtARoomTheUserIsNoLongerOn_IsNoContent()
    {
        // The sweep ran: the room survives because other people are in it, and this user does not.
        SeedPointer();
        await SeedRosterAsync(ChannelId, "someone-else");

        var result = await GetStateAsync();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task State_WhenTheCallerCanNoLongerSeeTheChannel_IsNoContent()
    {
        // A seat survives losing ViewChannel - the sweep is the only thing that removes a
        // participant, and it does not read roles - so without the permission re-check a user who
        // was role-stripped while offline would be told where they still are in a channel they are
        // no longer allowed to know about.
        SeedPointer(HiddenChannelId);
        await SeedRosterAsync(HiddenChannelId, UserId);

        var result = await GetStateAsync();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }
}
