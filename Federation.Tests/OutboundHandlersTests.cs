using AppEnvironment;
using Federation.Application.Bus.Outbound;
using Federation.Application.Services;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Federation.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Domain.Events.Conversation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Social.Contracts.Bus.Integration.Events;

namespace Federation.Tests;

[TestFixture]
public class OutboundHandlersTests
{
    private FakeFederationProvider _provider = null!;
    private UserService _userService = null!;
    private MicroserviceContext _db = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://home.example.com";

        _provider = new FakeFederationProvider();

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _bus = new FakeMessageBus();
        _userService = new UserService(cache, _bus);

        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"outbound-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<FederationInstance> SeedLinkedGuildAsync(string guildId, string host = "https://remote.example.com")
    {
        var instance = new FederationInstance { Id = $"fein_{Guid.NewGuid():N}", Host = host, Name = host, Status = FederationStatus.Active };
        _db.FederationInstances.Add(instance);
        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = guildId, RemoteId = guildId, ResourceType = FederatedResourceType.Guild, InstanceId = instance.Id });
        await _db.SaveChangesAsync();
        return instance;
    }

    // ── ConversationOutboundHandlers ─────────────────────────────────────────

    [Test]
    public async Task Conversation_Created_NoFederatedMembers_DoesNothing()
    {
        var message = new ConversationCreated { ConversationId = "conv1", MemberIds = ["localA", "localB"] };

        await ConversationOutboundHandlers.Handle(message, _provider, _userService, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    [Test]
    public async Task Conversation_Created_WithFederatedMember_CreatesRemoteConversation()
    {
        var message = new ConversationCreated { ConversationId = "conv1", MemberIds = ["localA", "usr_1:remote.example.com"] };

        await ConversationOutboundHandlers.Handle(message, _provider, _userService, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        var call = _provider.Calls[0];
        Assert.That(call.Method, Is.EqualTo(nameof(_provider.CreateConversationAsync)));
        Assert.That(call.Args[0], Is.EqualTo("conv1"));
        Assert.That(call.Args[2], Is.EqualTo("localA:https://home.example.com"));
    }

    [Test]
    public async Task Conversation_MemberAdded_FederatedUser_NotifiesRemote()
    {
        var message = new ConversationMemberAdded { ConversationId = "conv1", UserId = "usr_1:remote.example.com" };

        await ConversationOutboundHandlers.Handle(message, _provider, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.AddConversationMemberAsync)));
    }

    [Test]
    public async Task Conversation_MemberAdded_LocalUser_DoesNothing()
    {
        var message = new ConversationMemberAdded { ConversationId = "conv1", UserId = "localUser" };

        await ConversationOutboundHandlers.Handle(message, _provider, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    [Test]
    public async Task Conversation_MemberRemoved_FederatedUser_NotifiesRemote()
    {
        var message = new ConversationMemberRemoved { ConversationId = "conv1", UserId = "usr_1:remote.example.com" };

        await ConversationOutboundHandlers.Handle(message, _provider, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.RemoveConversationMemberAsync)));
    }

    [Test]
    public async Task Conversation_Deleted_AlwaysNotifiesRemote()
    {
        var message = new ConversationDeleted { ConversationId = "conv1" };

        await ConversationOutboundHandlers.Handle(message, _provider, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.DeleteConversationAsync)));
    }

    // ── GuildOutboundHandlers ────────────────────────────────────────────────

    [Test]
    public async Task Guild_MemberJoined_NoLinkedInstances_DoesNothing()
    {
        var message = new MemberJoinedForBots { GuildId = "gld_1", UserId = "usr_1" };

        await GuildOutboundHandlers.Handle(message, _provider, _userService, _db, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    [Test]
    public async Task Guild_MemberJoined_LinkedInstance_CallsJoinChannel()
    {
        await SeedLinkedGuildAsync("gld_1");
        var message = new MemberJoinedForBots { GuildId = "gld_1", UserId = "usr_1" };

        await GuildOutboundHandlers.Handle(message, _provider, _userService, _db, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.JoinChannelAsync)));
        Assert.That(_provider.Calls[0].Args[0], Is.EqualTo("gld_1:https://remote.example.com"));
    }

    [Test]
    public async Task Guild_MemberRemoved_ReasonBanned_CallsBanGuildMember()
    {
        await SeedLinkedGuildAsync("gld_1");
        var message = new MemberRemovedForBots { GuildId = "gld_1", UserId = "usr_1", Reason = "Banned" };

        await GuildOutboundHandlers.Handle(message, _provider, _userService, _db, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.BanGuildMemberAsync)));
    }

    [Test]
    public async Task Guild_MemberRemoved_OtherReason_TreatedAsLeave()
    {
        await SeedLinkedGuildAsync("gld_1");
        var message = new MemberRemovedForBots { GuildId = "gld_1", UserId = "usr_1", Reason = "Left" };

        await GuildOutboundHandlers.Handle(message, _provider, _userService, _db, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.LeaveChannelAsync)));
    }

    // ── MessagingOutboundHandlers ────────────────────────────────────────────

    [Test]
    public async Task Messaging_MessageCreated_FederatedAuthor_SkipsEcho()
    {
        var message = new MessageCreatedForChannel { ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_1:remote.example.com", Content = []};

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls, Is.Empty);
        Assert.That(_bus.Invoked, Is.Empty, "Should short-circuit before even resolving the channel's guild");
    }

    [Test]
    public async Task Messaging_MessageCreated_ChannelNotFound_DoesNothing()
    {
        _bus.ChannelResponse = new GetChannelResponse { Channel = null };
        var message = new MessageCreatedForChannel { ChannelId = "ch_1", MessageId = "m1", AuthorId = "localUser", Content = [] };

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    [Test]
    public async Task Messaging_MessageCreated_LinkedGuild_SendsToEachInstance()
    {
        await SeedLinkedGuildAsync("gld_1");
        _bus.ChannelResponse = new GetChannelResponse { Channel = new ChannelInfo { Id = "ch_1", GuildId = "gld_1", Name = "general", Type = "Text" } };
        var message = new MessageCreatedForChannel { ChannelId = "ch_1", MessageId = "m1", AuthorId = "localUser", Content = "hi"u8.ToArray() };

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.SendMessageAsync)));
        Assert.That(_provider.Calls[0].Args[0], Is.EqualTo("ch_1:https://remote.example.com"));
    }

    [Test]
    public async Task Messaging_ReactionCreated_FederatedUser_SkipsEcho()
    {
        var message = new ReactionCreatedEvent { ChannelId = "ch_1", MessageId = "m1", UserId = "usr_1:remote.example.com", Emoji = "👍" };

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    // ── SocialOutboundHandlers ───────────────────────────────────────────────

    [Test]
    public async Task Social_FriendRequestCreated_FederatedTarget_SendsRequest()
    {
        var message = new FriendRequestCreatedEvent { InitiatorUserId = "localUser", TargetUserId = "usr_1:remote.example.com" };

        await SocialOutboundHandlers.Handle(message, _provider, _userService, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.SendFriendRequestAsync)));
    }

    [Test]
    public async Task Social_FriendRequestCreated_LocalTarget_DoesNothing()
    {
        var message = new FriendRequestCreatedEvent { InitiatorUserId = "localUser", TargetUserId = "otherLocalUser" };

        await SocialOutboundHandlers.Handle(message, _provider, _userService, default);

        Assert.That(_provider.Calls, Is.Empty);
    }

    [Test]
    public async Task Social_FriendRemoved_FederatedTarget_NotifiesRemote()
    {
        var message = new FriendRemovedEvent { InitiatorUserId = "localUser", TargetUserId = "usr_1:remote.example.com" };

        await SocialOutboundHandlers.Handle(message, _provider, _userService, default);

        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.RemoveFriendAsync)));
    }
}
