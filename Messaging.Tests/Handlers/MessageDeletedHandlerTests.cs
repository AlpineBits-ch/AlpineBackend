using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Messages;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers MessageDeletedHandler: the MessageSearchEntry removal side effect and the dual
/// fan-out (conversation hub broadcast excluding the author vs channel bus forward).
/// </summary>
[TestFixture]
public class MessageDeletedHandlerTests
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

    [Test]
    public async Task Handle_SearchEntryExists_IsRemoved()
    {
        _context.MessageSearchEntries.Add(new MessageSearchEntry
        {
            MessageId = "msg-1",
            AuthorId = "author-1",
            Content = "content",
            CreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var handler = new MessageDeletedHandler();
        var evt = new MessageDeleted { MessageId = "msg-1", AuthorId = "author-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        // The handler only marks the entry Removed (see MessageDeletedHandler) and relies on
        // Wolverine's auto-wrap middleware to commit it after Handle returns - simulate that here.
        await _context.SaveChangesAsync();

        Assert.That(_context.MessageSearchEntries.Any(e => e.MessageId == "msg-1"), Is.False);
    }

    [Test]
    public void Handle_NoSearchEntry_DoesNotThrow()
    {
        var handler = new MessageDeletedHandler();
        var evt = new MessageDeleted { MessageId = "unindexed-msg", AuthorId = "author-1" };

        Assert.DoesNotThrowAsync(() => handler.Handle(evt, _hub, _context, _bus));
    }

    [Test]
    public async Task Handle_ConversationMessage_BroadcastsToConversationMembersOverHub()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "other-user", "conv-1"));
        await _context.SaveChangesAsync();

        var handler = new MessageDeletedHandler();
        var evt = new MessageDeleted { MessageId = "msg-1", ConversationId = "conv-1", AuthorId = "author-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
            Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.MessageDeleted"));
            Assert.That(_bus.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_ChannelMessage_ForwardsMessageDeletedForChannelOverBus()
    {
        var handler = new MessageDeletedHandler();
        var evt = new MessageDeleted { MessageId = "msg-1", ChannelId = "chan-1", AuthorId = "author-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        Assert.That(_bus.Sent, Has.Count.EqualTo(1));
        var forwarded = (MessageDeletedForChannel)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.MessageId, Is.EqualTo("msg-1"));
        });
    }

    [Test]
    public async Task Handle_NeitherConversationNorChannel_NoFanOut()
    {
        var handler = new MessageDeletedHandler();
        var evt = new MessageDeleted { MessageId = "msg-1", AuthorId = "author-1" };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Sent, Is.Empty);
        });
    }
}
