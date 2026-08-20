using Guild.Application.Bus.Events.Messages;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Covers the guild half of an edit's fan-out: <see cref="MessageUpdatedForChannelHandler"/> pushes
/// guild.MessageUpdated to the channel's viewers and republishes MessageUpdatedForBots.
/// </summary>
[TestFixture]
public class MessageUpdatedForChannelHandlerTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string AuthorUserId = "user-author";
    private const string ViewerUserId = "user-viewer";
    private const string EveryoneRoleId = "role-everyone";
    private const string Embeds = """[{"title":"card"}]""";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private ChannelAudienceService _audience = null!;
    private MessageUpdatedForChannelHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _audience = new ChannelAudienceService(
            PermissionTestFactory.Create(_cache, _context),
            new MemoryCache(new MemoryCacheOptions()));
        _handler = new MessageUpdatedForChannelHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = AuthorUserId, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d",
            Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
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

    private Task RunAsync(MessageUpdatedForChannel message)
    {
        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(new MemberPresenceState
            {
                MemberId = "memb-viewer", UserId = ViewerUserId,
                Status = OnlineStatus.Online.ToString(),
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }),
            NullLogger<GuildHydrateService>.Instance);

        return _handler.Handle(message, _hub, hydrate, _context, _cache, _bus,
            NullLogger<MessageUpdatedForChannelHandler>.Instance, _audience);
    }

    private static MessageUpdatedForChannel Edited(string? embedsJson) => new()
    {
        ChannelId = ChannelId,
        MessageId = "mesg-1",
        Content = "edited"u8.ToArray(),
        AuthorId = AuthorUserId,
        EmbedsJson = embedsJson,
    };

    [Test]
    public async Task Handle_BroadcastsTheEditToChannelViewersWithItsEmbeds()
    {
        await SeedGuildAsync();

        await RunAsync(Edited(Embeds));

        var hubClients = (FakeHubClients)_hub.Clients;
        var send = hubClients.SentMessages.Single(m => m.Method == "guild.MessageUpdated");
        var payload = (MessageUpdatedForChannel)send.Args[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(payload.EmbedsJson, Is.EqualTo(Embeds));
            Assert.That(payload.MessageId, Is.EqualTo("mesg-1"));
            Assert.That(hubClients.RecipientsOf("guild.MessageUpdated"), Does.Not.Contain(AuthorUserId),
                "the author already knows about their own edit");
        });
    }

    [Test]
    public async Task Handle_RepublishesForBotsWithTheEmbedsAndResolvedGuildId()
    {
        await SeedGuildAsync();

        await RunAsync(Edited(Embeds));

        var forBots = _bus.Published.OfType<MessageUpdatedForBots>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(forBots.GuildId, Is.EqualTo(GuildId));
            Assert.That(forBots.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(forBots.EmbedsJson, Is.EqualTo(Embeds));
        });
    }

    /// <summary>An edit that genuinely cleared the embeds still has to say so - "no embeds" is not
    /// the same thing as "do not mention embeds" once the payload is on the wire.</summary>
    [Test]
    public async Task Handle_ClearedEmbeds_AreForwardedAsCleared()
    {
        await SeedGuildAsync();

        await RunAsync(Edited("[]"));

        var forBots = _bus.Published.OfType<MessageUpdatedForBots>().Single();
        Assert.That(forBots.EmbedsJson, Is.EqualTo("[]"));
    }

    [Test]
    public async Task Handle_UnknownChannel_LogsAndDoesNothing()
    {
        await SeedGuildAsync();
        var message = Edited(Embeds);
        message.ChannelId = "chan-does-not-exist";

        await RunAsync(message);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Published.OfType<MessageUpdatedForBots>(), Is.Empty);
        });
    }
}
