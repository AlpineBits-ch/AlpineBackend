using Echo.Realtime;
using Messaging.Application.Handler.Realtime;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>Covers UpdateConversationReadHandler: updates the calling member's LastReadMessageId
/// for the given conversation, no-op if they aren't actually a member.</summary>
[TestFixture]
public class UpdateConversationReadHandlerTests
{
    private TestMessagingContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestMessagingContext(Guid.NewGuid().ToString());

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
    public async Task Handle_MemberExists_UpdatesLastReadMessageId()
    {
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        await UpdateConversationReadHandler.Handle(new UpdateConversationReadCommand("user-1", "conv-1", "msg-99"), _context);

        var stored = await _context.Members.FindAsync("m-1");
        Assert.That(stored!.LastReadMessageId, Is.EqualTo("msg-99"));
    }

    [Test]
    public async Task Handle_MemberDoesNotExist_IsNoOp()
    {
        Assert.DoesNotThrowAsync(() => UpdateConversationReadHandler.Handle(
            new UpdateConversationReadCommand("ghost", "conv-1", "msg-99"), _context));
    }

    [Test]
    public async Task Handle_OnlyUpdatesMatchingUserInConversation_NotOtherMembers()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        await UpdateConversationReadHandler.Handle(new UpdateConversationReadCommand("user-1", "conv-1", "msg-99"), _context);

        var other = await _context.Members.FindAsync("m-2");
        Assert.That(other!.LastReadMessageId, Is.Null);
    }
}
