using Guild.Application.Bus.Events.Messages;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// The guild half of a delete. Messaging only announces one to a conversation, so without this the
/// message stays on every other reader's screen until they reload.
/// </summary>
[TestFixture]
public class MessageDeletedForChannelHandlerTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string AuthorUserId = "user-author";
    private const string ViewerUserId = "user-viewer";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private ChannelAudienceService _audience = null!;
    private MessageDeletedForChannelHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _audience = new ChannelAudienceService(
            PermissionTestFactory.Create(_cache, _context),
            new MemoryCache(new MemoryCacheOptions()));
        _handler = new MessageDeletedForChannelHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task SeedGuildAsync(string? head = null, DateTimeOffset? headAt = null)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = AuthorUserId, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d",
            Type = ChannelType.Text, MessageCount = 4, CreatedAt = Now, UpdatedAt = Now,
            LastMessageId = head, LastActivityAt = headAt,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });

        AddMember("memb-author", AuthorUserId);
        AddMember("memb-viewer", ViewerUserId);

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

    private Task RunAsync(MessageDeletedForChannel message)
    {
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(new MemberPresenceState
            {
                MemberId = "memb-viewer", UserId = ViewerUserId,
                Status = OnlineStatus.Online.ToString(),
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }),
            NullLogger<GuildHydrateService>.Instance);

        return _handler.Handle(message, _context, _cache, _hub, hydrate, _audience, _bus,
            NullLogger<MessageDeletedForChannelHandler>.Instance);
    }

    private static MessageDeletedForChannel Deleted() => new()
    {
        ChannelId = ChannelId,
        MessageId = "mesg-1",
        AuthorId = AuthorUserId,
    };

    [Test]
    public async Task Handle_TellsTheChannelsViewersTheMessageIsGone()
    {
        await SeedGuildAsync();

        await RunAsync(Deleted());

        var hubClients = (FakeHubClients)_hub.Clients;

        Assert.Multiple(() =>
        {
            Assert.That(hubClients.RecipientsOf("guild.MessageDeleted"), Does.Contain(ViewerUserId));
            Assert.That(
                System.Text.Json.JsonSerializer.Serialize(
                    hubClients.SentMessages.Single(m => m.Method == "guild.MessageDeleted").Args[0]),
                Does.Contain("mesg-1"));
        });
    }

    [Test]
    public async Task Handle_StillRepublishesForBots()
    {
        await SeedGuildAsync();

        await RunAsync(Deleted());

        var forBots = _bus.Published.OfType<MessageDeletedForBots>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(forBots.GuildId, Is.EqualTo(GuildId));
            Assert.That(forBots.MessageId, Is.EqualTo("mesg-1"));
        });
    }

    [Test]
    public async Task Handle_UnknownChannel_SaysNothing()
    {
        await SeedGuildAsync();
        var message = Deleted();
        message.ChannelId = "chan-does-not-exist";

        await RunAsync(message);

        Assert.That(((FakeHubClients)_hub.Clients).SentMessages, Is.Empty);
    }

    /// <summary>
    /// The unread predicate compares LastActivityAt against the reader's cursor, so a head left on a
    /// deleted message is a channel nobody can ever mark read.
    /// </summary>
    [Test]
    public async Task Handle_DeletingTheHead_MovesItToWhatIsLeft()
    {
        var deletedAt = Now;
        var survivingAt = deletedAt.AddMinutes(-5);
        await SeedGuildAsync(head: "mesg-1", headAt: deletedAt);

        _bus.SetResponse<GetChannelHeadRequest>(new GetChannelHeadResponse
        {
            MessageId = "mesg-0",
            CreatedAt = survivingAt,
        });

        await RunAsync(Deleted());

        var channel = await _context.Channels.FindAsync(ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(channel!.LastMessageId, Is.EqualTo("mesg-0"));
            Assert.That(channel.LastActivityAt, Is.EqualTo(survivingAt));
            Assert.That(channel.MessageCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Handle_DeletingTheOnlyMessage_ClearsTheHead()
    {
        await SeedGuildAsync(head: "mesg-1", headAt: Now);

        _bus.SetResponse<GetChannelHeadRequest>(new GetChannelHeadResponse());

        await RunAsync(Deleted());

        var channel = await _context.Channels.FindAsync(ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(channel!.LastMessageId, Is.Null);
            Assert.That(channel.LastActivityAt, Is.Null);
        });
    }

    [Test]
    public async Task Handle_DeletingSomethingBehindTheHead_LeavesItAlone()
    {
        var headAt = Now;
        await SeedGuildAsync(head: "mesg-9", headAt: headAt);

        await RunAsync(Deleted());

        var channel = await _context.Channels.FindAsync(ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(channel!.LastMessageId, Is.EqualTo("mesg-9"));
            Assert.That(channel.LastActivityAt, Is.EqualTo(headAt));
            Assert.That(_bus.Invoked.OfType<GetChannelHeadRequest>(), Is.Empty);
            Assert.That(channel.MessageCount, Is.EqualTo(3));
        });
    }

    /// <summary>Messaging being unreachable leaves the stale head rather than blanking the channel.</summary>
    [Test]
    public async Task Handle_HeadLookupFails_LeavesTheHeadAlone()
    {
        var headAt = Now;
        await SeedGuildAsync(head: "mesg-1", headAt: headAt);

        await RunAsync(Deleted());

        var channel = await _context.Channels.FindAsync(ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(channel!.LastMessageId, Is.EqualTo("mesg-1"));
            Assert.That(channel.LastActivityAt, Is.EqualTo(headAt));
            Assert.That(channel.MessageCount, Is.EqualTo(3));
        });
    }
}
