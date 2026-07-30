using Echo.Realtime;
using Messaging.Application.Handler.Realtime;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>Covers StartConversationTypingHandler: pushes a typing indicator to every member of
/// the conversation (including the typer themselves - the handler doesn't filter them out, unlike
/// the call-broadcast handlers).</summary>
[TestFixture]
public class StartConversationTypingHandlerTests
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

    [Test]
    public async Task Handle_ConversationDoesNotExist_IsNoOp()
    {
        Assert.DoesNotThrowAsync(() => StartConversationTypingHandler.Handle(
            new StartConversationTypingCommand("user-1", "conv-missing"), _context, _hub));

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Handle_NotifiesEveryConversationMember()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members =
            [
                MakeMember("m-1", "user-1", "conv-1"),
                MakeMember("m-2", "user-2", "conv-1"),
            ],
        });
        await _context.SaveChangesAsync();

        await StartConversationTypingHandler.Handle(new StartConversationTypingCommand("user-1", "conv-1"), _context, _hub);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(2));
        Assert.That(hubClients.SentMessages.All(m => m.Method == "conversation.UserTyping"), Is.True);
    }
}
