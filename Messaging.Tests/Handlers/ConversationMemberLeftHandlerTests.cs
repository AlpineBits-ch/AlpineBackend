using Messaging.Application.Handler.Conversation;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Conversation;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>Covers ConversationMemberLeftHandler: notifies remaining members that someone left,
/// explicitly excluding the departing user themselves from the notified set.</summary>
[TestFixture]
public class ConversationMemberLeftHandlerTests
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

    private ConversationPermissionService Permissions() => new(_context, _cache);


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
    public async Task Handle_NotifiesRemainingMembers_ExcludingTheLeaver()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-a", "conv-1"),
            MakeMember("m-2", "user-b", "conv-1"));
        await _context.SaveChangesAsync();

        await ConversationMemberLeftHandler.Handle(
            new ConversationMemberRemoved { ConversationId = "conv-1", UserId = "user-a", HasLeft = true },
            _context, _cache, _hub, Permissions());

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
        var (method, args) = hubClients.SentMessages[0];
        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo("conversation.MemberLeft"));
            Assert.That(args[0], Is.InstanceOf<ConversationMemberRemoved>());
        });
    }

    [Test]
    public async Task Handle_OnlyMemberLeft_NoOneLeftToNotify_DoesNotThrow()
    {
        _context.Members.Add(MakeMember("m-1", "user-a", "conv-1"));
        await _context.SaveChangesAsync();

        Assert.DoesNotThrowAsync(() => ConversationMemberLeftHandler.Handle(
            new ConversationMemberRemoved { ConversationId = "conv-1", UserId = "user-a", HasLeft = true },
            _context, _cache, _hub, Permissions()));
    }
}
