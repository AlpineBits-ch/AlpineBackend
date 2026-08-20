using Echo.Realtime;
using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Response;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>Covers <see cref="GuildTypingHandler"/> under privacy spec T2-18.</summary>
[TestFixture]
public class GuildTypingHandlerTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";

    private const string TyperUserId = "user-typer";
    private const string WatcherUserId = "user-watcher";

    private TestGuildContext _context = null!;
    private FakeHubContext _hub = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private ChannelAudienceService _audience = null!;
    private GuildTypingHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _hub = new FakeHubContext();
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();
        _permissions = PermissionTestFactory.Create(_cache, _context);
        _audience = new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions()));
        _handler = new GuildTypingHandler();

        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "owner-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d",
            Type = ChannelType.Text, CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-typer", TyperUserId);
        AddMember("memb-watcher", WatcherUserId);

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void AddMember(string memberId, string userId)
    {
        var now = DateTimeOffset.UtcNow;
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(), CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rome-{memberId}", RoleId = EveryoneRoleId, MemberId = memberId,
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private static MemberPresenceState Present(string memberId, string userId) => new()
    {
        MemberId = memberId, UserId = userId, Status = nameof(OnlineStatus.Online),
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private Task RunAsync(
        UserPrivacySettingsSummary[] settings,
        (string Blocker, string Blocked)[]? blocks = null)
    {
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(
                Present("memb-typer", TyperUserId), Present("memb-watcher", WatcherUserId)),
            NullLogger<GuildHydrateService>.Instance);

        var privacy = PrivacyTestFactory.Privacy(_bus, new FakeDistributedCache(), settings);
        var blockCache = PrivacyTestFactory.Blocks(_bus, new FakeDistributedCache(), blocks ?? []);

        return _handler.Handle(
            new StartGuildTypingCommand(TyperUserId, ChannelId),
            _hub, hydrate, _context, _cache, _permissions, _audience, privacy, blockCache);
    }

    private List<string> TypingRecipients() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf("guild.UserTyping");

    private static UserPrivacySettingsSummary Typing(string userId, bool sends)
    {
        var settings = PrivacyTestFactory.Permissive(userId);
        settings.SendTypingIndicators = sends;
        return settings;
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Typing_WhenBothSidesSendIndicators_ReachesTheWatcher()
    {
        await RunAsync([Typing(TyperUserId, true), Typing(WatcherUserId, true)]);

        Assert.That(TypingRecipients(), Does.Contain(WatcherUserId));
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Typing_TheTyperStillReceivesTheirOwnIndicator()
    {
        // Their own clients need it to stay in sync across devices, and the reciprocity rule
        // cannot bite on a pair of one.
        await RunAsync([Typing(TyperUserId, true), Typing(WatcherUserId, true)]);

        Assert.That(TypingRecipients(), Does.Contain(TyperUserId));
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Typing_WhenTheTyperHasIndicatorsOff_IsNeverEmitted()
    {
        await RunAsync([Typing(TyperUserId, false), Typing(WatcherUserId, true)]);

        Assert.That(TypingRecipients(), Is.Empty);
    }

    [Test]
    public async Task Typing_ToAWatcherWhoDoesNotSendIndicators_IsWithheld()
    {
        // The reciprocity rule. The typer still emits; this particular recipient does not get it.
        await RunAsync([Typing(TyperUserId, true), Typing(WatcherUserId, false)]);

        Assert.Multiple(() =>
        {
            Assert.That(TypingRecipients(), Does.Not.Contain(WatcherUserId));
            Assert.That(TypingRecipients(), Does.Contain(TyperUserId));
        });
    }

    [Test]
    public async Task Typing_ToAWatcherInABlockedPair_IsWithheld()
    {
        await RunAsync(
            [Typing(TyperUserId, true), Typing(WatcherUserId, true)],
            [(WatcherUserId, TyperUserId)]);

        Assert.That(TypingRecipients(), Does.Not.Contain(WatcherUserId));
    }

    [Test]
    public async Task Typing_WhenIdentityIsUnreachable_IsNotEmittedAtAll()
    {
        // Fail closed: the typer's own SendTypingIndicators resolves restrictive, which is false.
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(
                Present("memb-typer", TyperUserId), Present("memb-watcher", WatcherUserId)),
            NullLogger<GuildHydrateService>.Instance);

        var deadBus = new FakeInvokingMessageBus();
        var privacy = PrivacyTestFactory.UnreachablePrivacy(deadBus, new FakeDistributedCache());
        var blocks = PrivacyTestFactory.Blocks(new FakeInvokingMessageBus(), new FakeDistributedCache());

        await _handler.Handle(
            new StartGuildTypingCommand(TyperUserId, ChannelId),
            _hub, hydrate, _context, _cache, _permissions, _audience, privacy, blocks);

        Assert.That(TypingRecipients(), Is.Empty);
    }
}
