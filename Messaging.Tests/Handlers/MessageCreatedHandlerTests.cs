using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Handler.Messages;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using DomainMessageType = global::Messaging.Domain.Enums.MessageType;

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

        await MessageCreatedHandler.Handle(MakeEvent(conversationId: "conv-1"), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

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

        await MessageCreatedHandler.Handle(MakeEvent(conversationId: "conv-1"), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

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
            MakeEvent(conversationId: "conv-1"), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance));
    }

    // ══════════════════════════════════════════════════════════════════════════ Handle - channel
    // branch ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ChannelMessage_SendsMessageCreatedForChannel()
    {
        var bus = new FakeMessageBus();

        await MessageCreatedHandler.Handle(MakeEvent(channelId: "chan-1"), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

        Assert.That(bus.Sent.Any(m => m is MessageCreatedForChannel evt && evt.ChannelId == "chan-1"), Is.True);
    }

    [Test]
    public async Task Handle_ChannelMessage_DoesNotBroadcastOverHub_OrQueryConversationMembers()
    {
        var bus = new FakeMessageBus();

        await MessageCreatedHandler.Handle(MakeEvent(channelId: "chan-1"), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

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

        await MessageCreatedHandler.Handle(evt, _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.That(forwarded.CreatedAt, Is.EqualTo(evt.CreatedAt));
    }

    [Test]
    public async Task Handle_ChannelMessage_MapsInviteType()
    {
        var bus = new FakeMessageBus();
        var evt = MakeEvent(channelId: "chan-1");
        evt.Type = DomainMessageType.Invite;

        await MessageCreatedHandler.Handle(evt, _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance);

        var forwarded = (MessageCreatedForChannel)bus.Sent.Single();
        Assert.That(forwarded.Type, Is.EqualTo(Guild.Contracts.Bus.Events.MessageType.Invite));
    }

    [Test]
    public async Task Handle_BothChannelAndConversationUnset_DoesNothing()
    {
        var bus = new FakeMessageBus();

        Assert.DoesNotThrowAsync(() => MessageCreatedHandler.Handle(
            MakeEvent(), _hub, _context, bus, NullLogger<MessageCreatedHandler>.Instance));
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
