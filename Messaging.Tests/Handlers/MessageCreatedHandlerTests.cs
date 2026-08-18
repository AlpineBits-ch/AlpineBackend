using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Handler.Messages;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using DomainMessageType = global::Messaging.Domain.Enums.MessageType;
using DomainAuthorIdType = global::Messaging.Domain.Enums.AuthorIdType;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers MessageCreatedHandler: the conversation-message branch (hub broadcast, cross-service
/// device-token lookup) and the channel-message branch (bus forward to Guild.Application as
/// MessageCreatedForChannel), plus the standalone LoadAsync helper.
/// </summary>
[TestFixture]
public class MessageCreatedHandlerTests
{
    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ConversationMember MakeMember(string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MessageCreated MakeEvent(string? conversationId = null, string? channelId = null) => new()
    {
        MessageId = "msg-1",
        Content = "hello"u8.ToArray(),
        ContextId = conversationId ?? channelId ?? "ctx-1",
        ConversationId = conversationId,
        ChannelId = channelId,
        AuthorId = "author-1",
        Attachments = [],
    };

    /// <summary>Block and privacy caches over a test's bus: nobody blocked, everybody on the
    /// product defaults (so <c>HidePushContent</c> is false and the payload is unchanged).</summary>
    private static BlockCache Blocks(FakeMessageBus bus, params BlockRelationship[] blocks) =>
        TestPrivacyServices.Build(bus, blocks: blocks).Blocks;

    private static PrivacySettingsCache Privacy(FakeMessageBus bus, params UserPrivacySettingsSummary[] settings) =>
        TestPrivacyServices.Build(bus, settings).Privacy;

    private static FakeMessageBus BusWithNoDeviceTokens() => new(msg => msg switch
    {
        GetProfileByUserIdRequest => new GetProfileByUserIdResponse { Profile = new ProfileDto { UserName = "author-1", Relationships = [] } },
        GetPushTokensForUsersRequest => new GetPushTokensForUsersResponse { Tokens = [] },
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    // ══════════════════════════════════════════════════════════════════════════
    // Handle - conversation branch
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ConversationMessage_BroadcastsToOtherMembers_ExcludingAuthor()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();

        await MessageCreatedHandler.Handle(MakeEvent(conversationId: "conv-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.MessageCreated"));
    }

    [Test]
    public async Task Handle_ConversationMessage_QueriesDeviceTokensForOtherMembersOnly()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"),
            MakeMember("m-3", "user-3", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();

        await MessageCreatedHandler.Handle(MakeEvent(conversationId: "conv-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var tokenRequest = (GetPushTokensForUsersRequest)bus.Invoked.Single(m => m is GetPushTokensForUsersRequest);
        Assert.That(tokenRequest.UserIds, Is.EquivalentTo(new[] { "user-2", "user-3" }));
    }

    [Test]
    public async Task Handle_ConversationMessage_NoOtherMembers_StillCompletesWithoutThrowing()
    {
        _context.Members.Add(MakeMember("m-1", "author-1", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();

        Assert.DoesNotThrowAsync(() => MessageCreatedHandler.Handle(
            MakeEvent(conversationId: "conv-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance));
    }

    [Test]
    public async Task Handle_ConversationSystemMessage_SendsNoPush()
    {
        // A call entry is a record of something the recipient already lived through - the call just
        // ended on their own device, or they watched it ring out.
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();
        var systemMessage = MakeEvent(conversationId: "conv-1");
        systemMessage.Type = DomainMessageType.CallEnded;

        await MessageCreatedHandler.Handle(systemMessage, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        Assert.That(bus.Invoked.Any(m => m is GetPushTokensForUsersRequest), Is.False);
    }

    [Test]
    public async Task Handle_ConversationSystemMessage_StillBroadcastsRealtime()
    {
        // Suppressing the push must not suppress delivery: the entry still has to appear in the
        // conversation, and the unread badge still has to be right.
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();
        var systemMessage = MakeEvent(conversationId: "conv-1");
        systemMessage.Type = DomainMessageType.CallMissed;

        await MessageCreatedHandler.Handle(systemMessage, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages.Single().Method, Is.EqualTo("conversation.MessageCreated"));
    }

    [Test]
    public async Task Handle_VoiceChannelInvite_BroadcastsToTheAuthorToo()
    {
        // The author is normally excluded because they sent the message and the send returned it.
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();
        var invite = MakeEvent(conversationId: "conv-1");
        invite.Type = DomainMessageType.VoiceChannelInvite;

        await MessageCreatedHandler.Handle(invite, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var send = ((FakeHubClients)_hub.Clients).Sends
            .Single(s => s.Method == "conversation.MessageCreated");

        Assert.Multiple(() =>
        {
            Assert.That(send.Target, Does.Contain("author-1"));
            Assert.That(send.Target, Does.Contain("user-2"));
        });
    }

    [Test]
    public async Task Handle_VoiceChannelInvite_StillSendsNoPush()
    {
        // Including the author in the realtime frame must not leak into the push list.
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();
        var invite = MakeEvent(conversationId: "conv-1");
        invite.Type = DomainMessageType.VoiceChannelInvite;

        await MessageCreatedHandler.Handle(invite, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        Assert.That(bus.Invoked.Any(m => m is GetPushTokensForUsersRequest), Is.False);
    }

    [Test]
    public async Task Handle_OrdinaryMessage_StillExcludesTheAuthorFromTheBroadcast()
    {
        // The exception above is scoped to server-authored types and nothing else.
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var bus = BusWithNoDeviceTokens();

        await MessageCreatedHandler.Handle(MakeEvent(conversationId: "conv-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var send = ((FakeHubClients)_hub.Clients).Sends
            .Single(s => s.Method == "conversation.MessageCreated");

        Assert.That(send.Target, Does.Not.Contain("author-1"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Handle - channel
    // branch ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ChannelMessage_SendsMessageCreatedForChannel()
    {
        var bus = new FakeMessageBus();

        await MessageCreatedHandler.Handle(MakeEvent(channelId: "chan-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        Assert.That(bus.Sent.Any(m => m is MessageCreatedForChannel evt && evt.ChannelId == "chan-1"), Is.True);
    }

    [Test]
    public async Task Handle_ChannelMessage_DoesNotBroadcastOverHub_OrQueryConversationMembers()
    {
        var bus = new FakeMessageBus();

        await MessageCreatedHandler.Handle(MakeEvent(channelId: "chan-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Is.Empty);
    }

    /// <summary>Guild writes this straight onto Channel.LastActivityAt, and its unread predicate
    /// compares it against ReadState.LastReadAt. Re-stamping it downstream instead of forwarding it
    /// would put the channel head slightly ahead of the message a cursor read resolves against, so
    /// the assertion is exact equality rather than a tolerance.</summary>
    [Test]
    public async Task Handle_ChannelMessage_ForwardsCreatedAtUnchanged()
    {
        var bus = new FakeMessageBus();
        var evt = MakeEvent(channelId: "chan-1");
        evt.CreatedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);

        await MessageCreatedHandler.Handle(evt, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.That(forwarded.CreatedAt, Is.EqualTo(evt.CreatedAt));
    }

    [Test]
    public async Task Handle_ChannelMessage_MapsInviteType()
    {
        var bus = new FakeMessageBus();
        var evt = MakeEvent(channelId: "chan-1");
        evt.Type = DomainMessageType.Invite;

        await MessageCreatedHandler.Handle(evt, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.That(forwarded.Type, Is.EqualTo(Guild.Contracts.Bus.Events.MessageType.Invite));
    }

    [Test]
    public async Task Handle_BothChannelAndConversationUnset_DoesNothing()
    {
        var bus = new FakeMessageBus();

        Assert.DoesNotThrowAsync(() => MessageCreatedHandler.Handle(
            MakeEvent(), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance));
    }

    /// <summary>Guild's realtime and bot fan-out render the character off these, so a persona
    /// message that arrives without them shows up as an ordinary one.</summary>
    [Test]
    public async Task Handle_ChannelMessage_ForwardsThePersonaIdentity()
    {
        var bus = new FakeMessageBus();
        var evt = MakeEvent(channelId: "chan-1");
        evt.AuthorIdType = DomainAuthorIdType.Persona;
        evt.PersonaId = "pers_cogsgrove";
        evt.AuthorDisplayName = "Mayor Cogsgrove";
        evt.AuthorAvatarUrl = "https://api.venta.gg/avatars/cogsgrove.png";

        await MessageCreatedHandler.Handle(evt, _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.AuthorIdType, Is.EqualTo(Guild.Contracts.Bus.Events.AuthorIdType.Persona));
            Assert.That(forwarded.PersonaId, Is.EqualTo("pers_cogsgrove"));
            Assert.That(forwarded.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(forwarded.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/cogsgrove.png"));

            // The costume never displaces the account the fan-out authorizes against.
            Assert.That(forwarded.AuthorId, Is.EqualTo("author-1"));
        });
    }

    [Test]
    public async Task Handle_ChannelMessage_OrdinaryMessage_ForwardsUserAuthorType()
    {
        var bus = new FakeMessageBus();

        await MessageCreatedHandler.Handle(MakeEvent(channelId: "chan-1"), _hub, _context, bus, Blocks(bus), Privacy(bus), StubWebPush.Create(bus).Sender, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.AuthorIdType, Is.EqualTo(Guild.Contracts.Bus.Events.AuthorIdType.User));
            Assert.That(forwarded.PersonaId, Is.Null);
            Assert.That(forwarded.AuthorDisplayName, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Push identity
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The leak this replaced: every push used the account name, so a persona message told
    /// the recipient's lock screen who plays the character.</summary>
    [Test]
    public void PushSenderName_PrefersTheMessagesOwnDisplayName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageCreatedHandler.PushSenderName("Mayor Cogsgrove", "real-account"), Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(MessageCreatedHandler.PushSenderAvatarUrl("https://cdn/persona.png", "https://cdn/account.png"),
                Is.EqualTo("https://cdn/persona.png"));
        });
    }

    [Test]
    public void PushSenderName_FallsBackToTheAccount()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageCreatedHandler.PushSenderName(null, "real-account"), Is.EqualTo("real-account"));
            Assert.That(MessageCreatedHandler.PushSenderName("   ", "real-account"), Is.EqualTo("real-account"));
            Assert.That(MessageCreatedHandler.PushSenderAvatarUrl(null, "https://cdn/account.png"),
                Is.EqualTo("https://cdn/account.png"));
        });
    }

    [Test]
    public void PushSenderName_WithNeither_UsesTheGenericTitle()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageCreatedHandler.PushSenderName(null, null), Is.EqualTo("New message"));
            Assert.That(MessageCreatedHandler.PushSenderAvatarUrl(null, null), Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ LoadAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task LoadAsync_ReturnsMembersExcludingAuthor()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var result = await MessageCreatedHandler.LoadAsync(MakeEvent(conversationId: "conv-1"), _context);

        Assert.That(result.Select(m => m.UserId), Is.EquivalentTo(new[] { "user-2" }));
    }
}
