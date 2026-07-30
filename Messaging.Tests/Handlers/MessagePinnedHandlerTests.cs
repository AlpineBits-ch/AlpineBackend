using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Messages;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using MessagePinned = Messaging.Domain.Events.Message.MessagePinned;
using MessageUnpinned = Messaging.Domain.Events.Message.MessageUnpinned;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers MessagePinnedHandler/MessageUnpinnedHandler's dual fan-out: conversation messages
/// broadcast directly to conversation members over the hub, channel messages instead forward a
/// *ForChannel event over the bus for Guild.Application to broadcast/audit-log.
/// </summary>
[TestFixture]
public class MessagePinnedHandlerTests
{
    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
        _bus = new FakeMessageBus();
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

    // ══════════════════════════════════════════════════════════════════════════
    // MessagePinnedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ConversationMessage_BroadcastsToConversationMembersOverHub()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-a", "conv-1"),
            MakeMember("m-2", "user-b", "conv-1"));
        await _context.SaveChangesAsync();

        var handler = new MessagePinnedHandler();
        var evt = new MessagePinned { MessageId = "msg-1", ConversationId = "conv-1", AuthorId = "author-1", PinnedById = "pinner-1", PinnedAt = DateTime.UtcNow };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
            Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.MessagePinned"));
            Assert.That(_bus.Sent, Is.Empty, "Conversation-only pin must not forward a channel event");
        });
    }

    [Test]
    public async Task Handle_ChannelMessage_ForwardsMessagePinnedForChannelOverBus()
    {
        var handler = new MessagePinnedHandler();
        var evt = new MessagePinned { MessageId = "msg-1", ChannelId = "chan-1", AuthorId = "author-1", PinnedById = "pinner-1", PinnedAt = DateTime.UtcNow };

        await handler.Handle(evt, _hub, _context, _bus);

        Assert.Multiple(() =>
        {
            Assert.That(_bus.Sent, Has.Count.EqualTo(1));
            var forwarded = (MessagePinnedForChannel)_bus.Sent[0];
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.MessageId, Is.EqualTo("msg-1"));
            Assert.That(forwarded.PinnedById, Is.EqualTo("pinner-1"));

            var hubClients = (FakeHubClients)_hub.Clients;
            Assert.That(hubClients.SentMessages, Is.Empty, "Channel-only pin must not broadcast to conversation members");
        });
    }

    [Test]
    public async Task Handle_NeitherConversationNorChannel_DoesNothing()
    {
        var handler = new MessagePinnedHandler();
        var evt = new MessagePinned { MessageId = "msg-1", AuthorId = "author-1", PinnedById = "pinner-1", PinnedAt = DateTime.UtcNow };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Sent, Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MessageUnpinnedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ConversationUnpin_BroadcastsToConversationMembersOverHub()
    {
        _context.Members.Add(MakeMember("m-1", "user-a", "conv-1"));
        await _context.SaveChangesAsync();

        var handler = new MessageUnpinnedHandler();
        var evt = new MessageUnpinned { MessageId = "msg-1", ConversationId = "conv-1", AuthorId = "author-1", UnpinnedById = "unpinner-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
            Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.MessageUnpinned"));
            Assert.That(_bus.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_ChannelUnpin_ForwardsMessageUnpinnedForChannelOverBus()
    {
        var handler = new MessageUnpinnedHandler();
        var evt = new MessageUnpinned { MessageId = "msg-1", ChannelId = "chan-1", AuthorId = "author-1", UnpinnedById = "unpinner-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        Assert.That(_bus.Sent, Has.Count.EqualTo(1));
        var forwarded = (MessageUnpinnedForChannel)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.UnpinnedById, Is.EqualTo("unpinner-1"));
        });
    }
}
