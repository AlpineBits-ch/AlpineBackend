using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Reaction;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Reactions;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers ReactionDeletedHandler's dual fan-out, mirroring ReactionCreatedHandlerTests.
/// </summary>
[TestFixture]
public class ReactionDeletedHandlerTests
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
        _context.Members.Add(MakeMember("m-1", "other-user", "conv-1"));
        await _context.SaveChangesAsync();

        var evt = new ReactionRemoved { MessageId = "msg-1", ConversationId = "conv-1", UserId = "reactor-1", Emoji = "👍" };

        await ReactionDeletedHandler.Handle(evt, _hub, _bus, _context);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
            Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.ReactionRemoved"));
            Assert.That(_bus.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_ChannelReaction_ForwardsReactionRemovedEventOverBus()
    {
        var evt = new ReactionRemoved { MessageId = "msg-1", ChannelId = "chan-1", UserId = "reactor-1", Emoji = "👍" };

        await ReactionDeletedHandler.Handle(evt, _hub, _bus, _context);

        Assert.That(_bus.Sent, Has.Count.EqualTo(1));
        var forwarded = (ReactionRemovedEvent)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.MessageId, Is.EqualTo("msg-1"));

            var hubClients = (FakeHubClients)_hub.Clients;
            Assert.That(hubClients.SentMessages, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_NeitherConversationNorChannel_NoFanOut()
    {
        var evt = new ReactionRemoved { MessageId = "msg-1", UserId = "reactor-1", Emoji = "👍" };

        await ReactionDeletedHandler.Handle(evt, _hub, _bus, _context);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Sent, Is.Empty);
        });
    }
}
