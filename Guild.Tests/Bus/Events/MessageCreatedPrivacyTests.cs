using System.Text;
using Guild.Application.Bus.Events.Messages;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// The privacy behaviour of <see cref="MessageCreatedHandler"/>: block enforcement across the
/// mention fan-out (T0-3) and push content privacy (T2-23).
/// </summary>
[TestFixture]
public class MessageCreatedPrivacyTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string MentionedRoleId = "role-mentioned";

    private const string AuthorUserId = "user-author";
    private const string AuthorMemberId = "memb-author";
    private const string BlockerUserId = "user-blocker";
    private const string BlockerMemberId = "memb-blocker";
    private const string PeerUserId = "user-peer";
    private const string PeerMemberId = "memb-peer";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private FakeInvokingMessageBus _integrationBus = null!;
    private ChannelAudienceService _audience = null!;
    private NotificationResolutionService _notifications = null!;
    private MessageCreatedHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _integrationBus = new FakeInvokingMessageBus();

        var permissions = PermissionTestFactory.Create(_cache, _context);
        _audience = new ChannelAudienceService(permissions, new MemoryCache(new MemoryCacheOptions()));
        _notifications = new NotificationResolutionService(_context);
        _handler = new MessageCreatedHandler();

        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = AuthorUserId,
            DefaultMessageNotifications = NotificationLevel.AllMessages,
            CreatedAt = now, UpdatedAt = now,
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
        _context.Roles.Add(new Role
        {
            Id = MentionedRoleId, GuildId = GuildId, Name = "team", Type = RoleType.None,
            Permissions = Permissions.ViewChannel,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember(AuthorMemberId, AuthorUserId);
        AddMember(BlockerMemberId, BlockerUserId);
        AddMember(PeerMemberId, PeerUserId);

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

    private void GiveMentionedRole(string memberId)
    {
        var now = DateTimeOffset.UtcNow;
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rome-team-{memberId}", RoleId = MentionedRoleId, MemberId = memberId,
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private static MemberPresenceState Present(string memberId, string userId) => new()
    {
        MemberId = memberId, UserId = userId, Status = nameof(OnlineStatus.Online),
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private static MessageCreatedForChannel Message(
        IEnumerable<string>? mentions = null, IEnumerable<string>? roleMentions = null, bool here = false) => new()
    {
        MessageId = "msg-1",
        ChannelId = ChannelId,
        AuthorId = AuthorUserId,
        Content = Encoding.UTF8.GetBytes("hello"),
        CreatedAt = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero),
        Mentions = mentions?.ToList() ?? [],
        RoleMentions = roleMentions?.ToList() ?? [],
        MentionsHere = here,
    };

    private Task RunAsync(
        MessageCreatedForChannel message,
        (string Blocker, string Blocked)[] blocks,
        UserPrivacySettingsSummary[] privacySettings,
        params MemberPresenceState[] presence)
    {
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(presence), NullLogger<GuildHydrateService>.Instance);

        var privacy = PrivacyTestFactory.Privacy(_integrationBus, new FakeDistributedCache(), privacySettings);
        var blockCache = PrivacyTestFactory.Blocks(_integrationBus, new FakeDistributedCache(), blocks);

        return _handler.Handle(
            message, _hub, hydrate, _context, _cache, _bus,
            NullLogger<MessageCreatedHandler>.Instance, _notifications, _audience, blockCache, privacy,
            PersonaMentions(), Scenes(hydrate), Joins(hydrate));
    }

    private PersonaMentionService PersonaMentions() =>
        new(_context, new PersonaService(_cache, _context));

    /// <summary>These tests seed no scene, so this only has to satisfy the handler's signature -
    /// the turn-advance branch never runs.</summary>
    private SceneService Scenes(GuildHydrateService hydrate) =>
        new(_context, PersonaMentions(), new PersonaCastService(_context), hydrate,
            PermissionTestFactory.Create(_cache, _context), _hub);

    /// <summary>Same reason as <see cref="Scenes"/>: no scene is seeded, so nothing here runs.</summary>
    private SceneJoinService Joins(GuildHydrateService hydrate)
    {
        var permissions = PermissionTestFactory.Create(_cache, _context);

        return new SceneJoinService(
            _context, Scenes(hydrate),
            new SceneVisibilityCache(_cache, _context, new PersonaService(_cache, _context)),
            permissions,
            new PersonaCastService(_context),
            new ModulePermissionHolderService(_context, permissions), _hub, _bus);
    }

    private static UserPrivacySettingsSummary[] EveryonePermissive() =>
    [
        PrivacyTestFactory.Permissive(AuthorUserId),
        PrivacyTestFactory.Permissive(BlockerUserId),
        PrivacyTestFactory.Permissive(PeerUserId),
    ];

    private List<string> IndexedUserIds() =>
        _bus.Published.OfType<IndexMentionsCommand>().SelectMany(c => c.Recipients).Select(r => r.UserId).ToList();

    private List<ChannelPushRequested> Pushes() => _bus.Published.OfType<ChannelPushRequested>().ToList();

    private List<string> PushedUserIds() => Pushes().SelectMany(p => p.UserIds).ToList();

    // ══════════════════════════════════════════════════════════════════════ Blocking (T0-3)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DirectMention_FromABlockedAuthor_ReachesNeitherTheIndexNorThePush()
    {
        await RunAsync(
            Message(mentions: [BlockerUserId, PeerUserId]),
            [(BlockerUserId, AuthorUserId)],
            EveryonePermissive());

        Assert.Multiple(() =>
        {
            Assert.That(IndexedUserIds(), Does.Not.Contain(BlockerUserId));
            Assert.That(IndexedUserIds(), Does.Contain(PeerUserId));
            Assert.That(PushedUserIds(), Does.Not.Contain(BlockerUserId));
        });
    }

    [Test]
    public async Task HereMention_FromABlockedAuthor_SkipsTheBlocker()
    {
        await RunAsync(
            Message(here: true),
            [(BlockerUserId, AuthorUserId)],
            EveryonePermissive(),
            Present(BlockerMemberId, BlockerUserId), Present(PeerMemberId, PeerUserId));

        Assert.Multiple(() =>
        {
            Assert.That(IndexedUserIds(), Does.Not.Contain(BlockerUserId));
            Assert.That(IndexedUserIds(), Does.Contain(PeerUserId));
        });
    }

    [Test]
    public async Task RoleMention_FromABlockedAuthor_DoesNotPushTheBlocker()
    {
        GiveMentionedRole(BlockerMemberId);
        GiveMentionedRole(PeerMemberId);
        await _context.SaveChangesAsync();

        await RunAsync(
            Message(roleMentions: [MentionedRoleId]),
            [(BlockerUserId, AuthorUserId)],
            EveryonePermissive());

        Assert.That(PushedUserIds(), Does.Not.Contain(BlockerUserId));
    }

    [Test]
    public async Task OrdinaryMessage_AtTheAllMessagesDefault_StillSkipsTheBlocker()
    {
        // The candidate set here is the whole membership, not the mention sets - so a filter
        // applied only to mentions would have let this one through.
        await RunAsync(Message(), [(BlockerUserId, AuthorUserId)], EveryonePermissive());

        Assert.Multiple(() =>
        {
            Assert.That(PushedUserIds(), Does.Not.Contain(BlockerUserId));
            Assert.That(PushedUserIds(), Does.Contain(PeerUserId));
        });
    }

    [Test]
    public async Task Blocking_IsSymmetric_WhenTheAuthorIsTheOneWhoBlocked()
    {
        await RunAsync(
            Message(mentions: [BlockerUserId]),
            [(AuthorUserId, BlockerUserId)],
            EveryonePermissive());

        Assert.That(IndexedUserIds(), Does.Not.Contain(BlockerUserId));
    }

    [Test]
    public async Task WithNoBlocks_EveryoneIsNotifiedAsBefore()
    {
        await RunAsync(Message(mentions: [BlockerUserId, PeerUserId]), [], EveryonePermissive());

        Assert.That(IndexedUserIds(), Is.EquivalentTo(new[] { BlockerUserId, PeerUserId }));
    }

    [Test]
    public async Task WhenSocialIsUnreachable_NobodyIsNotified()
    {
        // Fail closed. A block that stops applying during an outage is not a block.
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(), NullLogger<GuildHydrateService>.Instance);

        var deadBus = new FakeInvokingMessageBus();
        var blocks = PrivacyTestFactory.UnreachableBlocks(deadBus, new FakeDistributedCache());
        var privacy = PrivacyTestFactory.Privacy(
            new FakeInvokingMessageBus(), new FakeDistributedCache(), EveryonePermissive());

        await _handler.Handle(
            Message(mentions: [BlockerUserId, PeerUserId]), _hub, hydrate, _context, _cache, _bus,
            NullLogger<MessageCreatedHandler>.Instance, _notifications, _audience, blocks, privacy,
            PersonaMentions(), Scenes(hydrate), Joins(hydrate));

        Assert.Multiple(() =>
        {
            Assert.That(IndexedUserIds(), Is.Empty);
            Assert.That(PushedUserIds(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Push content privacy
    // (T2-23) ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Push_ForAUserWithHidePushContent_CarriesNoBodyAndNoAuthor()
    {
        var settings = EveryonePermissive();
        settings.Single(s => s.UserId == BlockerUserId).HidePushContent = true;

        await RunAsync(Message(), [], settings);

        var hidden = Pushes().Single(p => p.UserIds.Contains(BlockerUserId));

        Assert.Multiple(() =>
        {
            Assert.That(hidden.HideContent, Is.True);
            Assert.That(hidden.Content, Is.Empty);
            Assert.That(hidden.AuthorId, Is.Empty);
            Assert.That(hidden.MlsGeneration, Is.Null);

            // Routing ids survive - that is the whole point, the push still has to open the right
            // place when tapped.
            Assert.That(hidden.GuildId, Is.EqualTo(GuildId));
            Assert.That(hidden.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(hidden.MessageId, Is.EqualTo("msg-1"));
        });
    }

    [Test]
    public async Task Push_SplitsTheCohorts_LeavingEveryoneElsesPushIntact()
    {
        var settings = EveryonePermissive();
        settings.Single(s => s.UserId == BlockerUserId).HidePushContent = true;

        await RunAsync(Message(), [], settings);

        var plain = Pushes().Single(p => p.UserIds.Contains(PeerUserId));

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Has.Count.EqualTo(2));
            Assert.That(plain.HideContent, Is.False);
            Assert.That(plain.AuthorId, Is.EqualTo(AuthorUserId));
            Assert.That(Encoding.UTF8.GetString(plain.Content), Is.EqualTo("hello"));
            Assert.That(plain.UserIds, Does.Not.Contain(BlockerUserId));
        });
    }

    [Test]
    public async Task Push_WhenEveryRecipientHidesContent_PublishesOnlyTheHiddenEvent()
    {
        var settings = EveryonePermissive();
        foreach (var s in settings) s.HidePushContent = true;

        await RunAsync(Message(), [], settings);

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Has.Count.EqualTo(1));
            Assert.That(Pushes()[0].HideContent, Is.True);
        });
    }

    [Test]
    public async Task Push_WhenIdentityIsUnreachable_HidesContentFromEveryone()
    {
        // Restrictive default is HidePushContent = true.
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(), NullLogger<GuildHydrateService>.Instance);

        var privacy = PrivacyTestFactory.UnreachablePrivacy(
            new FakeInvokingMessageBus(), new FakeDistributedCache());
        var blocks = PrivacyTestFactory.Blocks(new FakeInvokingMessageBus(), new FakeDistributedCache());

        await _handler.Handle(
            Message(), _hub, hydrate, _context, _cache, _bus,
            NullLogger<MessageCreatedHandler>.Instance, _notifications, _audience, blocks, privacy,
            PersonaMentions(), Scenes(hydrate), Joins(hydrate));

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Has.Count.EqualTo(1));
            Assert.That(Pushes()[0].HideContent, Is.True);
            Assert.That(Pushes()[0].Content, Is.Empty);
        });
    }
}
