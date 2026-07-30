using Federation.Contracts.Materialization.Conversation;
using Messaging.Application.Bus.Federation;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Bus.Federation;

/// <summary>
/// Covers ConversationMaterializationHandlers: materializing remote DM-conversation federation
/// events as shadow Conversation/ConversationMember rows, idempotent by natural business key
/// rather than EventId.
/// </summary>
[TestFixture]
public class ConversationMaterializationHandlersTests
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

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedConversationCreatedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConversationCreated_NewConversation_CreatesShadowConversationWithMembers()
    {
        var message = new FederatedConversationCreatedReceived
        {
            EventId = "evt-1",
            OriginInstanceId = "instance-a",
            SenderId = "user-1:instance-a",
            ConversationId = "conv-remote-1",
            MemberIds = ["user-1:instance-a", "user-2:instance-a"],
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        var stored = _context.Conversations.SingleOrDefault(c => c.Id == "conv-remote-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.OriginInstanceId, Is.EqualTo("instance-a"));
        });
        Assert.That(_context.Members.Count(m => m.ConversationId == "conv-remote-1"), Is.EqualTo(2));
    }

    [Test]
    public async Task ConversationCreated_AlreadyExists_IsIdempotent_DoesNotDuplicate()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-remote-1",
            OriginInstanceId = "instance-a",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var message = new FederatedConversationCreatedReceived
        {
            EventId = "evt-2",
            OriginInstanceId = "instance-a",
            SenderId = "user-1:instance-a",
            ConversationId = "conv-remote-1",
            MemberIds = ["user-1:instance-a"],
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        Assert.That(_context.Conversations.Count(c => c.Id == "conv-remote-1"), Is.EqualTo(1));
        Assert.That(_context.Members.Any(m => m.ConversationId == "conv-remote-1"), Is.False,
            "Re-delivery of an already-materialized create must not re-add members");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedConversationMemberAddedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConversationMemberAdded_NewMember_AddsWithOriginInstanceStamped()
    {
        var message = new FederatedConversationMemberAddedReceived
        {
            EventId = "evt-3",
            OriginInstanceId = "instance-b",
            SenderId = "user-3:instance-b",
            ConversationId = "conv-1",
            UserId = "user-3:instance-b",
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        var member = _context.Members.SingleOrDefault(m => m.ConversationId == "conv-1" && m.UserId == "user-3:instance-b");
        Assert.Multiple(() =>
        {
            Assert.That(member, Is.Not.Null);
            Assert.That(member!.FederatedServerId, Is.EqualTo("instance-b"));
        });
    }

    [Test]
    public async Task ConversationMemberAdded_AlreadyExists_IsIdempotent_DoesNotDuplicate()
    {
        _context.Members.Add(MakeMember("m-1", "user-3:instance-b", "conv-1"));
        await _context.SaveChangesAsync();

        var message = new FederatedConversationMemberAddedReceived
        {
            EventId = "evt-4",
            OriginInstanceId = "instance-b",
            SenderId = "user-3:instance-b",
            ConversationId = "conv-1",
            UserId = "user-3:instance-b",
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        Assert.That(_context.Members.Count(m => m.ConversationId == "conv-1" && m.UserId == "user-3:instance-b"), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedConversationMemberLeftReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConversationMemberLeft_ExistingMember_IsRemoved()
    {
        _context.Members.Add(MakeMember("m-1", "user-3", "conv-1"));
        await _context.SaveChangesAsync();

        var message = new FederatedConversationMemberLeftReceived
        {
            EventId = "evt-5",
            OriginInstanceId = "instance-b",
            SenderId = "user-3",
            ConversationId = "conv-1",
            UserId = "user-3",
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        Assert.That(_context.Members.Any(m => m.ConversationId == "conv-1" && m.UserId == "user-3"), Is.False);
    }

    [Test]
    public async Task ConversationMemberLeft_MemberDoesNotExist_IsNoOp()
    {
        var message = new FederatedConversationMemberLeftReceived
        {
            EventId = "evt-6",
            OriginInstanceId = "instance-b",
            SenderId = "ghost",
            ConversationId = "conv-1",
            UserId = "ghost",
        };

        Assert.DoesNotThrowAsync(() => ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedConversationDeletedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConversationDeleted_ExistingConversation_RemovesConversationAndMembers()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "user-1", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var message = new FederatedConversationDeletedReceived
        {
            EventId = "evt-7",
            OriginInstanceId = "instance-b",
            SenderId = "user-1",
            ConversationId = "conv-1",
        };

        await ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None);

        Assert.That(_context.Conversations.Any(c => c.Id == "conv-1"), Is.False);
    }

    [Test]
    public async Task ConversationDeleted_ConversationDoesNotExist_IsNoOp()
    {
        var message = new FederatedConversationDeletedReceived
        {
            EventId = "evt-8",
            OriginInstanceId = "instance-b",
            SenderId = "ghost",
            ConversationId = "conv-ghost",
        };

        Assert.DoesNotThrowAsync(() => ConversationMaterializationHandlers.Handle(message, _context, CancellationToken.None));
    }
}
