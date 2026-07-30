using Echo.Realtime;
using Messaging.Application.Handler.Conversation;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Conversation;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Tests.Handlers;

/// <summary>Covers ConversationDeletedHandler: fans the deletion out over the hub to every
/// remaining member (the conversation row itself may already be gone by the time this handler
/// runs, per ConversationEndpoints.DeleteConversation's ordering).</summary>
[TestFixture]
public class ConversationDeletedHandlerTests
{
    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
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

    [Test]
    public async Task Handle_NotifiesAllRemainingMembers()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-a", "conv-1"),
            MakeMember("m-2", "user-b", "conv-1"));
        await _context.SaveChangesAsync();

        await ConversationDeletedHandler.Handle(
            new ConversationDeleted { ConversationId = "conv-1" }, _context, _cache, _hub);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
        var (method, args) = hubClients.SentMessages[0];
        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo("conversation.ConversationDeleted"));
            Assert.That(args[0], Is.InstanceOf<ConversationDeleted>());
        });
    }

    [Test]
    public async Task Handle_NoMembers_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => ConversationDeletedHandler.Handle(
            new ConversationDeleted { ConversationId = "conv-empty" }, _context, _cache, _hub));
    }
}
