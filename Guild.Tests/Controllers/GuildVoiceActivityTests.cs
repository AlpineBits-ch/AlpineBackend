using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
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
/// Covers the guild-level voice activity index and the endpoint that reads it - the "someone is in
/// voice in this server" indicator in the server list.
/// </summary>
[TestFixture]
public class GuildVoiceActivityTests
{
    private const string GuildId = "guild-visible";
    private const string HiddenGuildId = "guild-hidden";
    private const string ChannelId = "channel-visible";
    private const string HiddenChannelId = "channel-hidden";
    private const string MemberId = "user-member";
    private const string OwnerId = "user-owner";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildVoiceActivityStore _activity = null!;
    private StreamViewerStore _viewers = null!;
    private GuildVoiceController _voice = null!;
    private GuildVoiceActivityController _activityController = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        var locks = new FakeDistributedLockService();
        var voiceStore = new LockedJsonCacheStore(locks, _cache);
        _activity = new GuildVoiceActivityStore(locks, _cache);
        _viewers = new StreamViewerStore(locks, _cache);

        var permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);

        _voice = new GuildVoiceController(
            permissions, _hub, _cache, voiceStore, locks,
            StubCloudflareHttp.CreateService(), _context,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            _activity, _viewers, _bus)
        {
            ControllerContext = Context(MemberId),
        };

        _activityController = new GuildVoiceActivityController(_activity, permissions, _context)
        {
            ControllerContext = Context(MemberId),
        };

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ControllerContext Context(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(userId) },
    };

    /// <summary>Two guilds the member belongs to.</summary>
    private async Task SeedAsync()
    {
        await SeedGuildAsync(GuildId, ChannelId, "role-visible", "member-visible",
            Permissions.ViewChannel | Permissions.Connect | Permissions.Speak);
        await SeedGuildAsync(HiddenGuildId, HiddenChannelId, "role-hidden", "member-hidden",
            Permissions.None);
        await _context.SaveChangesAsync();
    }

    private Task SeedGuildAsync(string guildId, string channelId, string roleId, string memberId, Permissions permissions)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = guildId, OwnerId = OwnerId, Name = guildId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = channelId, GuildId = guildId, Name = channelId, Description = "d",
            Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = roleId, GuildId = guildId, Name = "member", Permissions = permissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = guildId, UserId = MemberId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{MemberId}#{guildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-{roleId}", RoleId = roleId, MemberId = memberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }

    private async Task<List<GuildVoiceActivityDto>> GetActivityAsync()
    {
        var result = await _activityController.GetVoiceActivity(CancellationToken.None);
        return (List<GuildVoiceActivityDto>)((OkObjectResult)result).Value!;
    }

    /// <summary>Puts someone in a channel the way the sweep and the move path do - straight into
    /// the index - for the cases that are about reading it rather than maintaining it.</summary>
    private Task IndexAsync(string guildId, string channelId, string userId) =>
        _activity.AddParticipantAsync(guildId, channelId, userId);

    // ══════════════════════════════════════════════════════════════════════════
    // Maintenance: ordinary joins and leaves keep the index true
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Join_IndexesTheParticipantAgainstTheGuild()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);

        var activity = await _activity.LoadAsync(GuildId);
        Assert.That(activity!.Channels[ChannelId].UserIds, Is.EqualTo(new[] { MemberId }));
    }

    [Test]
    public async Task Leave_RemovesThemAgain()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await _voice.Leave(GuildId, ChannelId, CancellationToken.None);

        var activity = await _activity.LoadAsync(GuildId);
        Assert.That(activity!.Channels, Is.Empty);
    }

    [Test]
    public async Task Leave_AlsoDropsTheirWatchClaims()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await _viewers.WatchAsync(StreamViewerStore.ChannelScope(ChannelId), "share-1", MemberId);

        await _voice.Leave(GuildId, ChannelId, CancellationToken.None);

        var snapshot = await _viewers.SnapshotAsync(StreamViewerStore.ChannelScope(ChannelId));
        Assert.That(snapshot, Is.Empty,
            "someone who is not in the channel cannot be watching a stream inside it");
    }

    [Test]
    public async Task Leave_ForgetsTheAudienceOfSharesTheLeaverOwned()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await SetActiveShareAsync(MemberId, "share-1");
        await _viewers.WatchAsync(StreamViewerStore.ChannelScope(ChannelId), "share-1", "user-watcher");

        await _voice.Leave(GuildId, ChannelId, CancellationToken.None);

        var snapshot = await _viewers.SnapshotAsync(StreamViewerStore.ChannelScope(ChannelId));
        Assert.That(snapshot.ContainsKey("share-1"), Is.False,
            "the stream ended when its owner left - its viewer count is meaningless, not zero");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Reading: what a member is allowed to be told
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task VoiceActivity_WithNobodyInVoice_IsEmpty()
    {
        Assert.That(await GetActivityAsync(), Is.Empty);
    }

    [Test]
    public async Task VoiceActivity_ReportsAChannelTheMemberCanSee()
    {
        await IndexAsync(GuildId, ChannelId, "user-other");

        var activity = await GetActivityAsync();

        Assert.Multiple(() =>
        {
            Assert.That(activity, Has.Count.EqualTo(1));
            Assert.That(activity[0].GuildId, Is.EqualTo(GuildId));
            Assert.That(activity[0].ParticipantCount, Is.EqualTo(1));
            Assert.That(activity[0].Channels.Single().ChannelId, Is.EqualTo(ChannelId));
        });
    }

    [Test]
    public async Task VoiceActivity_HidesAChannelTheMemberCannotView()
    {
        await IndexAsync(HiddenGuildId, HiddenChannelId, "user-other");

        var activity = await GetActivityAsync();

        Assert.That(activity, Is.Empty,
            "a count is a disclosure: it says people are in a room the caller may not know exists");
    }

    [Test]
    public async Task VoiceActivity_OmitsAGuildWhoseOnlyOccupiedChannelIsHidden()
    {
        await IndexAsync(GuildId, ChannelId, "user-other");
        await IndexAsync(HiddenGuildId, HiddenChannelId, "user-other");

        var activity = await GetActivityAsync();

        Assert.That(activity.Select(a => a.GuildId), Is.EqualTo(new[] { GuildId }),
            "the guild must not be listed at all - a bare 'something is happening here' still leaks it");
    }

    [Test]
    public async Task VoiceActivity_ExcludesGuildsTheCallerIsNotIn()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = "guild-stranger", OwnerId = OwnerId, Name = "stranger",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
        await IndexAsync("guild-stranger", "channel-stranger", "user-other");

        Assert.That(await GetActivityAsync(), Is.Empty);
    }

    [Test]
    public async Task VoiceActivity_SurfacesWhoIsLive()
    {
        await IndexAsync(GuildId, ChannelId, "user-other");
        await _activity.SetStreamingAsync(GuildId, ChannelId, "user-other", true);

        var activity = await GetActivityAsync();

        Assert.Multiple(() =>
        {
            Assert.That(activity[0].HasStream, Is.True);
            Assert.That(activity[0].Channels.Single().StreamerIds, Is.EqualTo(new[] { "user-other" }));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Watching a share
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Watch_CountsTheCallerAndTellsTheChannel()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await SetActiveShareAsync(MemberId, "share-1");

        var result = await _voice.WatchShare(GuildId, ChannelId, "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.That(payload, Does.Contain("\"viewerCount\":1"));
        Assert.That(_hub.Clients as FakeHubClients, Is.Not.Null);
        Assert.That(((FakeHubClients)_hub.Clients).SentMessages.Any(m => m.Method == "guild.voice.ShareViewersChanged"),
            Is.True, "other participants only learn the count from this event");
    }

    [Test]
    public async Task Unwatch_StopsCountingThem()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await SetActiveShareAsync(MemberId, "share-1");
        await _voice.WatchShare(GuildId, ChannelId, "share-1", CancellationToken.None);

        var result = await _voice.UnwatchShare(GuildId, ChannelId, "share-1", CancellationToken.None);

        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.That(payload, Does.Contain("\"viewerCount\":0"));
    }

    [Test]
    public async Task Watch_ByANonParticipant_IsRefused()
    {
        // Seeded straight into the roster so a share exists, but the caller never joined.
        await SeedChannelStateAsync("user-other", "share-1");

        var result = await _voice.WatchShare(GuildId, ChannelId, "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<ForbidResult>(),
            "media only flows inside the channel, so a count from outside it is a fabrication");
    }

    [Test]
    public async Task Watch_OfAShareNobodyIsPublishing_IsRefused()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);

        var result = await _voice.WatchShare(GuildId, ChannelId, "share-nonexistent", CancellationToken.None);

        Assert.That(result, Is.TypeOf<NotFoundResult>(),
            "otherwise any participant could mint viewer counts for streams that do not exist");
    }

    [Test]
    public async Task Viewers_AreReadableByAnyoneWhoCanSeeTheChannel()
    {
        await _voice.Join(GuildId, ChannelId, CancellationToken.None);
        await SetActiveShareAsync(MemberId, "share-1");
        await _voice.WatchShare(GuildId, ChannelId, "share-1", CancellationToken.None);

        var result = await _voice.GetShareViewers(GuildId, ChannelId, CancellationToken.None);

        var viewers = (Dictionary<string, IReadOnlyList<string>>)((OkObjectResult)result).Value!;
        Assert.That(viewers["share-1"], Is.EqualTo(new[] { MemberId }));
    }

    // ── Seeding helpers ───────────────────────────────────────────────────────

    /// <summary>Records an active share against an existing roster entry, the way
    /// GuildCloudflareController does once the screen tracks are published.</summary>
    private async Task SetActiveShareAsync(string userId, string shareId)
    {
        var raw = await _cache.GetStringAsync(ChannelVoiceState.GetCacheKey(ChannelId));
        var state = JsonSerializer.Deserialize<ChannelVoiceState>(raw!)!;
        var participant = state.Participants.First(p => p.UserId == userId);
        participant.IsStreaming = true;
        participant.ActiveScreenShares.Add(new ActiveScreenShare { ShareId = shareId, TrackNames = [$"screen-{shareId}"] });
        _cache.SetEntry(ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(state));
    }

    private Task SeedChannelStateAsync(string userId, string shareId)
    {
        var state = new ChannelVoiceState
        {
            ChannelId = ChannelId,
            GuildId = GuildId,
            Participants =
            [
                new VoiceState
                {
                    UserId = userId, ChannelId = ChannelId, GuildId = GuildId, IsStreaming = true,
                    ActiveScreenShares = [new ActiveScreenShare { ShareId = shareId, TrackNames = [$"screen-{shareId}"] }],
                },
            ],
        };
        _cache.SetEntry(ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(state));
        return Task.CompletedTask;
    }
}
