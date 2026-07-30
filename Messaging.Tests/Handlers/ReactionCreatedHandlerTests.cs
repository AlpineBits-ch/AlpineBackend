using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Reaction;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Reactions;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers ReactionCreatedHandler's dual fan-out: conversation reactions broadcast directly to
/// members (excluding the reacting user) over the hub, channel reactions instead forward a
/// Guild.Contracts ReactionCreatedEvent over the bus for Guild.Application to broadcast.
/// </summary>
[TestFixture]
public class ReactionCreatedHandlerTests
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
    public async Task Handle_ConversationReaction_BroadcastsOverHub()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "reactor-1", "conv-1"),
            MakeMember("m-2", "other-user", "conv-1"));
        await _context.SaveChangesAsync();

        var evt = new ReactionCreated { MessageId = "msg-1", ConversationId = "conv-1", UserId = "reactor-1", Emoji = "👍" };

        await ReactionCreatedHandler.Handle(evt, _hub, _bus, _context);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
            Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.ReactionCreated"));
            Assert.That(_bus.Sent, Is.Empty, "Conversation-only reaction must not forward a channel event");
        });
    }

    [Test]
    public async Task Handle_ChannelReaction_ForwardsReactionCreatedEventOverBus()
    {
        var evt = new ReactionCreated { MessageId = "msg-1", ChannelId = "chan-1", UserId = "reactor-1", Emoji = "👍", EmojiId = "custom-1" };

        await ReactionCreatedHandler.Handle(evt, _hub, _bus, _context);

        Assert.That(_bus.Sent, Has.Count.EqualTo(1));
        var forwarded = (ReactionCreatedEvent)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.MessageId, Is.EqualTo("msg-1"));
            Assert.That(forwarded.Emoji, Is.EqualTo("👍"));
            Assert.That(forwarded.EmojiId, Is.EqualTo("custom-1"));

            var hubClients = (FakeHubClients)_hub.Clients;
            Assert.That(hubClients.SentMessages, Is.Empty, "Channel-only reaction must not broadcast to conversation members");
        });
    }

    [Test]
    public async Task Handle_NeitherConversationNorChannel_NoFanOut()
    {
        var evt = new ReactionCreated { MessageId = "msg-1", UserId = "reactor-1", Emoji = "👍" };

        await ReactionCreatedHandler.Handle(evt, _hub, _bus, _context);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Sent, Is.Empty);
        });
    }
}
