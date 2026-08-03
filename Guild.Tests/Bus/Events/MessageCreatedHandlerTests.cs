using Guild.Application.Bus.Events.Messages;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Messaging.Contracts.Bus.Commands;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Covers <see cref="MessageCreatedHandler"/>, which had no tests before. Three things are being
/// pinned down here:
///
/// <list type="number">
/// <item><b>Mention resolution.</b> Who a message mentions, split by how - direct, by role, by
/// @everyone, by @here - because the three suppression settings act on those kinds separately.</item>
///
/// <item><b>That the work is set-based.</b> The mention badge used to be a SELECT plus an upsert
/// per mentioned member, run sequentially, so one @everyone in a 5,000-member guild was 5,000
/// sequential round trips on the message-create path. The counts are asserted rather than the query
/// count, but the fixture seeds enough members that a reintroduced loop would be visible.</item>
///
/// <item><b>That the existing behaviour survived the rewrite.</b> The realtime broadcast, the bot
/// republish and the thread activity touch all live in the same method and are asserted unchanged.</item>
/// </list>
/// </summary>
[TestFixture]
public class MessageCreatedHandlerTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string OwnerUserId = "user-owner";
    private const string AuthorUserId = "user-author";
    private const string AuthorMemberId = "memb-author";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private ChannelAudienceService _audience = null!;
    private NotificationResolutionService _notifications = null!;
    private MessageCreatedHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _audience = new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions()));
        _notifications = new NotificationResolutionService(_context);
        _handler = new MessageCreatedHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════
    // Seeding
    // ══════════════════════════════════════════════════════════════════════

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>Seeds a guild with an @everyone role carrying ViewChannel, one text channel, the
    /// author, and <paramref name="memberCount"/> other members (memb-1..memb-N / user-1..user-N).</summary>
    private async Task SeedGuildAsync(
        int memberCount,
        NotificationLevel guildDefault = NotificationLevel.AllMessages,
        ChannelType channelType = ChannelType.Text)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = OwnerUserId,
            DefaultMessageNotifications = guildDefault,
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d",
            Type = channelType, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });

        AddMember(AuthorMemberId, AuthorUserId);
        for (var i = 1; i <= memberCount; i++) AddMember($"memb-{i}", $"user-{i}");

        await _context.SaveChangesAsync();
    }

    private void AddMember(string memberId, string userId)
    {
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(), CreatedAt = Now, UpdatedAt = Now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rome-{memberId}", RoleId = EveryoneRoleId, MemberId = memberId,
            CreatedAt = Now, UpdatedAt = Now,
        });
    }

    private async Task AddRoleAsync(string roleId, params string[] memberIds)
    {
        _context.Roles.Add(new Role
        {
            Id = roleId, GuildId = GuildId, Name = roleId, Type = RoleType.None,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });
        foreach (var memberId in memberIds)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rome-{roleId}-{memberId}", RoleId = roleId, MemberId = memberId,
                CreatedAt = Now, UpdatedAt = Now,
            });
        }
        await _context.SaveChangesAsync();
    }

    private static MessageCreatedForChannel Message(
        IEnumerable<string>? mentions = null,
        IEnumerable<string>? roleMentions = null,
        bool everyone = false,
        bool here = false,
        string authorId = AuthorUserId) => new()
    {
        ChannelId = ChannelId,
        MessageId = "mesg-1",
        Content = "hi"u8.ToArray(),
        AuthorId = authorId,
        CreatedAt = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
        Mentions = mentions?.ToList() ?? [],
        RoleMentions = roleMentions?.ToList() ?? [],
        MentionsEveryone = everyone,
        MentionsHere = here,
    };

    private static MemberPresenceState Present(string memberId, string userId, OnlineStatus status) => new()
    {
        MemberId = memberId, UserId = userId, Status = status.ToString(),
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private Task RunAsync(MessageCreatedForChannel message, params MemberPresenceState[] presence)
    {
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(presence), NullLogger<GuildHydrateService>.Instance);

        return _handler.Handle(
            message, _hub, hydrate, _context, _cache, _bus,
            NullLogger<MessageCreatedHandler>.Instance, _notifications, _audience);
    }

    /// <summary>Every recipient across the chunked index commands. Mentions are no longer a counter
    /// on ReadState - they are rows in Messaging's index, published from here - so this is where the
    /// assertions have to look.</summary>
    private List<MentionRecipient> IndexedRecipients() =>
        _bus.Published.OfType<IndexMentionsCommand>().SelectMany(c => c.Recipients).ToList();

    /// <summary>Recipients as member ids, so the expectations below stay readable. memb-N maps to
    /// user-N by construction of the fixture.</summary>
    private List<string> MentionedMemberIds() =>
        IndexedRecipients()
            .Select(r => r.UserId == AuthorUserId ? AuthorMemberId : r.UserId.Replace("user-", "memb-"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private int MentionCountFor(string memberId)
    {
        var userId = memberId == AuthorMemberId ? AuthorUserId : memberId.Replace("memb-", "user-");
        return IndexedRecipients().Count(r => r.UserId == userId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Direct mentions
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DirectMention_BumpsOnlyThatMember()
    {
        await SeedGuildAsync(memberCount: 3);

        await RunAsync(Message(mentions: ["user-2"]));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-2" }).AsCollection);
    }

    [Test]
    public async Task DirectMention_CarriesTheMessageCoordinatesIntoTheIndex()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(Message(mentions: ["user-1"]));

        var command = _bus.Published.OfType<IndexMentionsCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.MessageId, Is.EqualTo("mesg-1"));
            Assert.That(command.GuildId, Is.EqualTo(GuildId));
            Assert.That(command.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(command.CreatedAt, Is.EqualTo(new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero)));
            Assert.That(command.Recipients.Single().Kind, Is.EqualTo("Direct"));
        });
    }

    /// <summary>Mentions no longer touch ReadState at all. Conflating the two is what made the old
    /// counter impossible to keep honest.</summary>
    [Test]
    public async Task DirectMention_LeavesTheReadCursorAlone()
    {
        await SeedGuildAsync(memberCount: 1);
        _context.ReadStates.Add(new ReadState
        {
            Id = "reta-1", ChannelId = ChannelId, MemberId = "memb-1",
            LastReadMessageId = "mesg-old", LastReadAt = Now.AddDays(-1),
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message(mentions: ["user-1"]));
        await _context.SaveChangesAsync();

        Assert.That(_context.ReadStates.AsNoTracking().Single().LastReadMessageId, Is.EqualTo("mesg-old"));
    }

    [Test]
    public async Task DirectMention_OfSomebodyInAnotherGuild_IsIgnored()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(Message(mentions: ["user-from-elsewhere"]));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.Empty);
    }

    /// <summary>Mentioning yourself is not a mention. The realtime broadcast has always excluded the
    /// author; the badge did not, so an @everyone gave its own sender an unread mention.</summary>
    [Test]
    public async Task SelfMention_ProducesNothing()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(Message(mentions: [AuthorUserId]));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Role mentions
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Like @everyone, a role ping is one row - its holders are reconstructable at read
    /// time from RoleMember.CreatedAt, so nothing goes in the per-user index.</summary>
    [Test]
    public async Task RoleMention_IndexesNobodyAndWritesOneRow()
    {
        await SeedGuildAsync(memberCount: 3);
        await AddRoleAsync("role-mods", "memb-1", "memb-3");

        await RunAsync(Message(roleMentions: ["role-mods"]));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(IndexedRecipients(), Is.Empty);
            Assert.That(Broadcasts(), Has.Count.EqualTo(1));
        });
    }

    /// <summary>Named directly and holding a mentioned role is one index entry, not two. The role
    /// ping is a separate broadcast row; the read side merges them.</summary>
    [Test]
    public async Task DirectAndRoleMentionOfTheSameMember_IndexesOnce()
    {
        await SeedGuildAsync(memberCount: 1);
        await AddRoleAsync("role-mods", "memb-1");

        await RunAsync(Message(mentions: ["user-1"], roleMentions: ["role-mods"]));
        await _context.SaveChangesAsync();

        Assert.That(MentionCountFor("memb-1"), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════
    // @everyone
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The point of the broadcast design: an @everyone names nobody individually, so it
    /// puts nothing in the per-user index however large the guild is.</summary>
    [Test]
    public async Task Everyone_IndexesNobody()
    {
        await SeedGuildAsync(memberCount: 4);

        await RunAsync(Message(everyone: true));
        await _context.SaveChangesAsync();

        Assert.That(IndexedRecipients(), Is.Empty);
    }

    /// <summary>1,200 members, one row, nothing indexed. The old shape was 1,200 sequential round
    /// trips and 1,200 rows for the same ping.</summary>
    [Test]
    public async Task Everyone_InALargeGuild_CostsOneRowAndNoIndexWrites()
    {
        await SeedGuildAsync(memberCount: 1_200);

        await RunAsync(Message(everyone: true));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Broadcasts(), Has.Count.EqualTo(1));
            Assert.That(IndexedRecipients(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // @here
    //
    // The only mention whose audience is a moment in time rather than durable state, so it is the
    // only one that has to be resolved against the presence snapshot.
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Here_BumpsOnlyMembersWhoAreOnline()
    {
        await SeedGuildAsync(memberCount: 3);

        await RunAsync(
            Message(here: true),
            Present("memb-1", "user-1", OnlineStatus.Online),
            Present("memb-2", "user-2", OnlineStatus.Online));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-1", "memb-2" }).AsCollection);
    }

    /// <summary>"Here" means at the keyboard. Presence covers everyone holding a live connection, so
    /// without a status filter idle, do-not-disturb and invisible members were all counted as
    /// present.</summary>
    [TestCase(OnlineStatus.Idle)]
    [TestCase(OnlineStatus.DoNotDisturb)]
    [TestCase(OnlineStatus.Hidden)]
    [TestCase(OnlineStatus.Offline)]
    public async Task Here_SkipsMembersWhoAreConnectedButNotOnline(OnlineStatus status)
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(
            Message(here: true),
            Present("memb-1", "user-1", status),
            Present("memb-2", "user-2", OnlineStatus.Online));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-2" }).AsCollection);
    }

    [Test]
    public async Task Here_SkipsTheAuthorEvenWhenTheyAreOnline()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(
            Message(here: true),
            Present(AuthorMemberId, AuthorUserId, OnlineStatus.Online),
            Present("memb-1", "user-1", OnlineStatus.Online));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-1" }).AsCollection);
    }

    /// <summary>Presence is guild-wide but this event is channel-scoped. Unfiltered, an @here in a
    /// private channel bumped the badge of every online member of the guild - including those
    /// without ViewChannel, who cannot open the channel to clear it.</summary>
    [Test]
    public async Task Here_SkipsMembersWhoCannotSeeTheChannel()
    {
        await SeedGuildAsync(memberCount: 2);

        // Deny ViewChannel to memb-2 specifically, leaving them present but not an audience member.
        // SendMessages has to go with it: GuildPermissionService expands anything implying
        // SendMessages back into ViewChannel, so denying the latter alone is a no-op.
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = ChannelId, RoleId = null, MemberId = "memb-2",
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(
            Message(here: true),
            Present("memb-1", "user-1", OnlineStatus.Online),
            Present("memb-2", "user-2", OnlineStatus.Online));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-1" }).AsCollection);
    }

    [Test]
    public async Task Here_WithNobodyPresent_ProducesNothingAndDoesNotThrow()
    {
        await SeedGuildAsync(memberCount: 2);

        Assert.DoesNotThrowAsync(() => RunAsync(Message(here: true)));
        await _context.SaveChangesAsync();

        Assert.That(MentionedMemberIds(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Failing gracefully
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnknownChannel_LogsAndReturnsWithoutThrowing()
    {
        await SeedGuildAsync(memberCount: 1);
        var message = Message(mentions: ["user-1"]);
        message.ChannelId = "chan-does-not-exist";

        Assert.DoesNotThrowAsync(() => RunAsync(message));
        await _context.SaveChangesAsync();

        Assert.That(IndexedRecipients(), Is.Empty);
    }

    [Test]
    public async Task NoMentionsAtAll_PublishesNoIndexCommand()
    {
        await SeedGuildAsync(memberCount: 3);

        await RunAsync(Message());

        Assert.That(_bus.Published.OfType<IndexMentionsCommand>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Regression guards - behaviour that shares this method and must not have moved
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task StillBroadcastsTheMessageToChannelViewers()
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(
            Message(),
            Present("memb-1", "user-1", OnlineStatus.Online),
            Present("memb-2", "user-2", OnlineStatus.Idle));

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages.Any(m => m.Method == "guild.MessageCreated"), Is.True,
            "the realtime broadcast reaches everyone present, online or not - only @here cares about status");
    }

    [Test]
    public async Task StillRepublishesForBotsWithTheResolvedGuildId()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(Message());

        var forBots = _bus.Published.OfType<MessageCreatedForBots>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(forBots.GuildId, Is.EqualTo(GuildId));
            Assert.That(forBots.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(forBots.MessageId, Is.EqualTo("mesg-1"));
        });
    }

    [Test]
    public async Task StillTouchesThreadActivity()
    {
        await SeedGuildAsync(memberCount: 1, channelType: ChannelType.Thread);

        await RunAsync(Message());
        await _context.SaveChangesAsync();

        var thread = _context.Channels.AsNoTracking().Single(c => c.Id == ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(thread.LastActivityAt, Is.Not.Null);
            Assert.That(thread.MessageCount, Is.EqualTo(1));
        });
    }

    /// <summary>Activity is now denormalized onto every channel type, not just forum posts: it is
    /// the head-of-channel the inbox unread predicate compares against each read cursor.</summary>
    [Test]
    public async Task PlainTextChannel_AlsoGetsItsActivityTouched()
    {
        await SeedGuildAsync(memberCount: 1);

        await RunAsync(Message());
        await _context.SaveChangesAsync();

        var channel = _context.Channels.AsNoTracking().Single(c => c.Id == ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(channel.LastActivityAt, Is.EqualTo(new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero)),
                "taken from the message's own CreatedAt, not a fresh UtcNow - a reading taken here would sit ahead of the message a cursor read resolves against");
            Assert.That(channel.LastMessageId, Is.EqualTo("mesg-1"));
            Assert.That(channel.MessageCount, Is.EqualTo(1));
            Assert.That(channel.AutoArchiveAt, Is.Null, "auto-archive stays thread-only; a text channel has no deadline to slide");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Broadcast mentions - one row per ping, not one per recipient
    // ══════════════════════════════════════════════════════════════════════

    private List<ChannelBroadcastMention> Broadcasts() =>
        _context.ChannelBroadcastMentions.AsNoTracking().OrderBy(b => b.RoleId).ToList();

    [Test]
    public async Task Everyone_WritesExactlyOneBroadcastRow()
    {
        await SeedGuildAsync(memberCount: 500);

        await RunAsync(Message(everyone: true));
        await _context.SaveChangesAsync();

        var rows = Broadcasts();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1), "500 members, one row - the recipient set is reconstructable, so it is not materialised");
            Assert.That(rows[0].Kind, Is.EqualTo(BroadcastMentionKind.Everyone));
            Assert.That(rows[0].RoleId, Is.Null);
            Assert.That(rows[0].MessageCreatedAt, Is.EqualTo(new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero)));
        });
    }

    [Test]
    public async Task RoleMentions_WriteOneRowPerRole()
    {
        await SeedGuildAsync(memberCount: 2);
        await AddRoleAsync("role-a", "memb-1");
        await AddRoleAsync("role-b", "memb-2");

        await RunAsync(Message(roleMentions: ["role-a", "role-b"]));
        await _context.SaveChangesAsync();

        var rows = Broadcasts();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(r => r.RoleId), Is.EqualTo(new[] { "role-a", "role-b" }).AsCollection);
            Assert.That(rows.All(r => r.Kind == BroadcastMentionKind.Role), Is.True);
        });
    }

    [Test]
    public async Task DuplicateRoleMentionsInOneMessage_WriteOneRow()
    {
        await SeedGuildAsync(memberCount: 1);
        await AddRoleAsync("role-a", "memb-1");

        await RunAsync(Message(roleMentions: ["role-a", "role-a", "role-a"]));
        await _context.SaveChangesAsync();

        Assert.That(Broadcasts(), Has.Count.EqualTo(1));
    }

    /// <summary>Wolverine retries. The read path counts these rows, so a re-run must not double the
    /// ping - which is what the (MessageId, RoleId) unique index is for.</summary>
    [Test]
    public async Task ReplayingTheSameMessage_DoesNotDuplicateBroadcastRows()
    {
        await SeedGuildAsync(memberCount: 2);
        await AddRoleAsync("role-a", "memb-1");

        await RunAsync(Message(everyone: true, roleMentions: ["role-a"]));
        await _context.SaveChangesAsync();

        await RunAsync(Message(everyone: true, roleMentions: ["role-a"]));
        await _context.SaveChangesAsync();

        Assert.That(Broadcasts(), Has.Count.EqualTo(2), "one @everyone row and one @role row, however many times the handler runs");
    }

    [Test]
    public async Task DirectMentionsOnly_WriteNoBroadcastRow()
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(Message(mentions: ["user-1"]));
        await _context.SaveChangesAsync();

        Assert.That(Broadcasts(), Is.Empty);
    }

    /// <summary>@here is the one broadcast that is <em>not</em> recorded as a row. Its audience is
    /// the presence snapshot, which is gone by the time anything reads the row - so it has to be
    /// materialised per recipient while the answer still exists.</summary>
    [Test]
    public async Task Here_WritesNoBroadcastRow()
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(Message(here: true), Present("memb-1", "user-1", OnlineStatus.Online));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Broadcasts(), Is.Empty);
            Assert.That(MentionedMemberIds(), Is.EqualTo(new[] { "memb-1" }).AsCollection);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Push recipients
    // ══════════════════════════════════════════════════════════════════════

    private ChannelPushRequested? PushRequest() => _bus.Published.OfType<ChannelPushRequested>().SingleOrDefault();

    /// <summary>Nobody to notify means no event is published at all, so this flattens "no event"
    /// and "event with an empty list" to the same thing - callers care about who was reached.</summary>
    private List<string> PushedUserIds() => PushRequest()?.UserIds.ToList() ?? [];

    [Test]
    public async Task AllMessagesGuild_PushesToEveryDisconnectedMemberEvenWithoutAMention()
    {
        await SeedGuildAsync(memberCount: 3, guildDefault: NotificationLevel.AllMessages);

        await RunAsync(Message());

        Assert.That(PushedUserIds(), Is.EquivalentTo(new[] { "user-1", "user-2", "user-3" }));
    }

    /// <summary>The whole point of the guild default: at OnlyMentions an ordinary message must not
    /// resolve a recipient set spanning the membership.</summary>
    [Test]
    public async Task OnlyMentionsGuild_PushesToNobodyForAnUnmentionedMessage()
    {
        await SeedGuildAsync(memberCount: 3, guildDefault: NotificationLevel.OnlyMentions);

        await RunAsync(Message());

        Assert.That(PushedUserIds(), Is.Empty);
    }

    [Test]
    public async Task OnlyMentionsGuild_StillPushesToAMentionedMember()
    {
        await SeedGuildAsync(memberCount: 3, guildDefault: NotificationLevel.OnlyMentions);

        await RunAsync(Message(mentions: ["user-2"]));

        Assert.That(PushedUserIds(), Is.EqualTo(new[] { "user-2" }).AsCollection);
    }

    /// <summary>The candidate narrowing is an optimisation, so it has to stay a superset: a member
    /// who opted back into AllMessages under a quiet guild default must still be found.</summary>
    [Test]
    public async Task OnlyMentionsGuild_StillPushesToAMemberWhoOptedBackIntoAllMessages()
    {
        await SeedGuildAsync(memberCount: 3, guildDefault: NotificationLevel.OnlyMentions);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "memb-3", Level = NotificationLevel.AllMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message());

        Assert.That(PushedUserIds(), Is.EqualTo(new[] { "user-3" }).AsCollection);
    }

    [Test]
    public async Task OnlyMentionsGuild_StillPushesToAMemberWithAnAllMessagesChannelOverride()
    {
        await SeedGuildAsync(memberCount: 2, guildDefault: NotificationLevel.OnlyMentions);
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = "memb-2", ChannelId = ChannelId,
            Level = NotificationLevel.AllMessages, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message());

        Assert.That(PushedUserIds(), Is.EqualTo(new[] { "user-2" }).AsCollection);
    }

    [Test]
    public async Task ConnectedMembers_AreNotPushedTo()
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(Message(), Present("memb-1", "user-1", OnlineStatus.Online));

        Assert.That(PushedUserIds(), Is.EqualTo(new[] { "user-2" }).AsCollection);
    }

    [Test]
    public async Task MutedMember_IsNotPushedToEvenWhenDirectlyMentioned()
    {
        await SeedGuildAsync(memberCount: 2);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "memb-1", Level = NotificationLevel.AllMessages,
            MutedUntil = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message(mentions: ["user-1"]));

        Assert.That(PushedUserIds(), Does.Not.Contain("user-1"));
    }

    [Test]
    public async Task MobilePushDisabled_SuppressesThePushButNotTheMentionBadge()
    {
        await SeedGuildAsync(memberCount: 1);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "memb-1", Level = NotificationLevel.AllMessages,
            MobilePush = false, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message(mentions: ["user-1"]));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(PushedUserIds(), Is.Empty);
            Assert.That(MentionCountFor("memb-1"), Is.EqualTo(1), "the in-app mention is not what MobilePush switches off");
        });
    }

    [Test]
    public async Task SuppressEveryone_DropsAnEveryonePushButKeepsADirectOne()
    {
        await SeedGuildAsync(memberCount: 2, guildDefault: NotificationLevel.OnlyMentions);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "memb-1", Level = NotificationLevel.OnlyMentions,
            SuppressEveryone = true, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message(everyone: true));
        Assert.That(PushedUserIds(), Does.Not.Contain("user-1"));

        _bus.Published.Clear();
        await RunAsync(Message(mentions: ["user-1"]));
        Assert.That(PushedUserIds(), Does.Contain("user-1"));
    }

    [Test]
    public async Task SuppressRoleMentions_DropsARolePushButKeepsADirectOne()
    {
        await SeedGuildAsync(memberCount: 2, guildDefault: NotificationLevel.OnlyMentions);
        await AddRoleAsync("role-mods", "memb-1");
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "memb-1", Level = NotificationLevel.OnlyMentions,
            SuppressRoleMentions = true, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await RunAsync(Message(roleMentions: ["role-mods"]));
        Assert.That(PushedUserIds(), Does.Not.Contain("user-1"));

        _bus.Published.Clear();
        await RunAsync(Message(mentions: ["user-1"]));
        Assert.That(PushedUserIds(), Does.Contain("user-1"));
    }

    /// <summary>An @here reaches the people who were here. Someone offline was not, so it must not
    /// notify them - which under the old code it did, because the flag was applied to every
    /// candidate rather than to the presence snapshot.</summary>
    [Test]
    public async Task Here_DoesNotPushToAnOfflineMemberOfAnOnlyMentionsGuild()
    {
        await SeedGuildAsync(memberCount: 2, guildDefault: NotificationLevel.OnlyMentions);

        await RunAsync(Message(here: true));

        Assert.That(PushedUserIds(), Is.Empty);
    }

    [Test]
    public async Task AuthorIsNeverPushedTo()
    {
        await SeedGuildAsync(memberCount: 2);

        await RunAsync(Message(everyone: true));

        Assert.That(PushedUserIds(), Does.Not.Contain(AuthorUserId));
    }
}
