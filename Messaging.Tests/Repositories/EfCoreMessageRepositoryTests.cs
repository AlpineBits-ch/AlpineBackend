using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Covers EfCoreMessageRepository (the Postgres/InMemory-testable IMessageRepository
/// implementation - see ScyllaMessageRepository for the Cassandra counterpart, not unit tested
/// here per repo convention) against the EF Core InMemory provider.
///
/// EfCoreMessageRepository's mutating methods never call SaveChangesAsync themselves - every
/// call site is Wolverine-managed (bus handler or WolverineHttp endpoint), which auto-commits the
/// ambient DbContext once the handler/endpoint returns. A manual save inside the repository would
/// double-save against the same tracked DbContext Wolverine already commits. So these tests call
/// SaveAsync() after each mutating repo call to simulate that auto-commit, the same way
/// Guild.Tests' endpoint tests simulate WolverineHttp's transaction middleware.
/// </summary>
[TestFixture]
public class EfCoreMessageRepositoryTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task SaveAsync() => _context.SaveChangesAsync();

    private static Message MakeMessage(string? channelId = null, string? conversationId = "conv-1", string authorId = "author-1") => Message.Create(new CreateMessageParams
    {
        Content = "hello"u8.ToArray(),
        ChannelId = channelId,
        ConversationId = conversationId,
        AuthorId = authorId,
    });

    // ══════════════════════════════════════════════════════════════════════════
    // CreateMessageAsync / GetMessageAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateMessageAsync_PersistsMessage()
    {
        var message = MakeMessage();

        var created = await _repo.CreateMessageAsync(message);
        await SaveAsync();

        Assert.That(created.Id, Is.EqualTo(message.Id));
        var fetched = await _repo.GetMessageAsync(message.Id);
        Assert.That(fetched, Is.Not.Null);
    }

    [Test]
    public async Task GetMessageAsync_UnknownId_ReturnsNull()
    {
        var result = await _repo.GetMessageAsync("does-not-exist");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMessageAsync_IncludesAttachments()
    {
        var message = MakeMessage();
        message.Attachments.Add(MinimalAttachment.Create(new CreateMinimalAttachmentParams
        {
            Id = "att-1",
            FileName = "file.png",
            ContentType = "image/png",
        }));
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        var fetched = await _repo.GetMessageAsync(message.Id);

        Assert.That(fetched!.Attachments, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UpdateMessageAsync / DeleteMessageAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateMessageAsync_PersistsChanges()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        message.Content = "updated"u8.ToArray();
        await _repo.UpdateMessageAsync(message);
        await SaveAsync();

        var fetched = await _repo.GetMessageAsync(message.Id);
        Assert.That(System.Text.Encoding.UTF8.GetString(fetched!.Content), Is.EqualTo("updated"));
    }

    [Test]
    public async Task DeleteMessageAsync_RemovesMessage()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        await _repo.DeleteMessageAsync(message);
        await SaveAsync();

        var fetched = await _repo.GetMessageAsync(message.Id);
        Assert.That(fetched, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pin / Unpin / GetPinnedMessages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PinMessageAsync_SetsIsPinnedAndMetadata()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        var pinned = await _repo.PinMessageAsync(message, "pinner-1");
        await SaveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pinned.IsPinned, Is.True);
            Assert.That(pinned.PinnedById, Is.EqualTo("pinner-1"));
            Assert.That(pinned.PinnedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task UnpinMessageAsync_ClearsIsPinnedAndMetadata()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();
        await _repo.PinMessageAsync(message, "pinner-1");
        await SaveAsync();

        var unpinned = await _repo.UnpinMessageAsync(message);
        await SaveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(unpinned.IsPinned, Is.False);
            Assert.That(unpinned.PinnedById, Is.Null);
            Assert.That(unpinned.PinnedAt, Is.Null);
        });
    }

    [Test]
    public async Task GetPinnedMessagesAsync_ReturnsOnlyPinnedForContext()
    {
        var pinnedMsg = MakeMessage(conversationId: "ctx-1");
        var unpinnedMsg = MakeMessage(conversationId: "ctx-1");
        var otherContextMsg = MakeMessage(conversationId: "ctx-2");

        await _repo.CreateMessageAsync(pinnedMsg);
        await _repo.CreateMessageAsync(unpinnedMsg);
        await _repo.CreateMessageAsync(otherContextMsg);
        await SaveAsync();

        await _repo.PinMessageAsync(pinnedMsg, "pinner-1");
        await _repo.PinMessageAsync(otherContextMsg, "pinner-1");
        await SaveAsync();

        var result = await _repo.GetPinnedMessagesAsync("ctx-1");

        Assert.That(result.Select(m => m.Id), Is.EquivalentTo(new[] { pinnedMsg.Id }));
    }

    [Test]
    public async Task GetPinnedMessagesAsync_OrdersByMostRecentlyPinnedFirst()
    {
        var first = MakeMessage(conversationId: "ctx-1");
        var second = MakeMessage(conversationId: "ctx-1");
        await _repo.CreateMessageAsync(first);
        await _repo.CreateMessageAsync(second);
        await SaveAsync();

        await _repo.PinMessageAsync(first, "pinner-1");
        first.PinnedAt = DateTime.UtcNow.AddMinutes(-10);
        await _repo.UpdateMessageAsync(first);
        await SaveAsync();

        await _repo.PinMessageAsync(second, "pinner-1");
        await SaveAsync();

        var result = (await _repo.GetPinnedMessagesAsync("ctx-1")).ToList();

        Assert.That(result[0].Id, Is.EqualTo(second.Id), "Most recently pinned message must come first");
    }

    [Test]
    public async Task GetPinnedMessagesAsync_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var msg = MakeMessage(conversationId: "ctx-1");
            await _repo.CreateMessageAsync(msg);
            await SaveAsync();
            await _repo.PinMessageAsync(msg, "pinner-1");
            await SaveAsync();
        }

        var result = await _repo.GetPinnedMessagesAsync("ctx-1", limit: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetPinnedMessagesAsync_NoPinnedMessages_ReturnsEmpty()
    {
        var result = await _repo.GetPinnedMessagesAsync("ctx-empty");
        Assert.That(result, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Reactions
    // ══════════════════════════════════════════════════════════════════════════

    private static Reaction MakeReaction(string messageId, string userId, string emoji = "👍", string? conversationId = "conv-1", string? channelId = null) =>
        Reaction.Create(new CreateReactionParams
        {
            MessageId = messageId,
            UserId = userId,
            Emoji = emoji,
            ConversationId = conversationId,
            ChannelId = channelId,
        });

    [Test]
    public async Task AddReactionAsync_PersistsReaction()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        await _repo.AddReactionAsync(MakeReaction(message.Id, "user-1"));
        await SaveAsync();

        Assert.That(_context.Reactions.ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RemoveReactionAsync_RemovesOnlyMatchingReaction()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await _repo.AddReactionAsync(MakeReaction(message.Id, "user-1", "👍"));
        await _repo.AddReactionAsync(MakeReaction(message.Id, "user-2", "👍"));
        await _repo.AddReactionAsync(MakeReaction(message.Id, "user-1", "🎉"));
        await SaveAsync();

        await _repo.RemoveReactionAsync("conv-1", message.Id, "👍", "user-1");
        await SaveAsync();

        var remaining = _context.Reactions.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(2));
            Assert.That(remaining.Any(r => r.UserId == "user-1" && r.Emoji == "👍"), Is.False);
            Assert.That(remaining.Any(r => r.UserId == "user-2" && r.Emoji == "👍"), Is.True);
            Assert.That(remaining.Any(r => r.UserId == "user-1" && r.Emoji == "🎉"), Is.True);
        });
    }

    [Test]
    public async Task RemoveReactionAsync_NoMatch_DoesNotThrow()
    {
        var message = MakeMessage();
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        Assert.DoesNotThrowAsync(() => _repo.RemoveReactionAsync("conv-1", message.Id, "👍", "nobody"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetMessagesByConversationIdAsync / GetMessagesByChannelIdAsync / GetMessagesByContextIdAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesByConversationIdAsync_ReturnsInAscendingOrder_WithReactions()
    {
        var first = MakeMessage(conversationId: "conv-x");
        await Task.Delay(5);
        var second = MakeMessage(conversationId: "conv-x");
        await _repo.CreateMessageAsync(first);
        await _repo.CreateMessageAsync(second);
        await _repo.AddReactionAsync(MakeReaction(first.Id, "user-1", conversationId: "conv-x"));
        await SaveAsync();

        var (messages, reactions) = await _repo.GetMessagesByConversationIdAsync("conv-x", take: 10, skip: 0);
        var list = messages.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(list, Has.Count.EqualTo(2));
            Assert.That(list[0].Id, Is.EqualTo(first.Id), "Results must be ascending by CreatedAt");
            Assert.That(list[1].Id, Is.EqualTo(second.Id));
            Assert.That(reactions[first.Id], Has.Count.EqualTo(1));
            Assert.That(reactions[second.Id], Is.Empty);
        });
    }

    [Test]
    public async Task GetMessagesByConversationIdAsync_OtherConversation_NotIncluded()
    {
        var inScope = MakeMessage(conversationId: "conv-x");
        var outOfScope = MakeMessage(conversationId: "conv-y");
        await _repo.CreateMessageAsync(inScope);
        await _repo.CreateMessageAsync(outOfScope);
        await SaveAsync();

        var (messages, _) = await _repo.GetMessagesByConversationIdAsync("conv-x", take: 10, skip: 0);

        Assert.That(messages.Select(m => m.Id), Is.EquivalentTo(new[] { inScope.Id }));
    }

    [Test]
    public async Task GetMessagesByChannelIdAsync_ReturnsChannelMessagesOnly()
    {
        var channelMsg = MakeMessage(channelId: "chan-1", conversationId: null);
        var conversationMsg = MakeMessage(conversationId: "conv-1");
        await _repo.CreateMessageAsync(channelMsg);
        await _repo.CreateMessageAsync(conversationMsg);
        await SaveAsync();

        var (messages, _) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 10, skip: 0);

        Assert.That(messages.Select(m => m.Id), Is.EquivalentTo(new[] { channelMsg.Id }));
    }

    [Test]
    public async Task GetMessagesByContextIdAsync_ReturnsMatchingContextOnly()
    {
        var message = MakeMessage(conversationId: "conv-ctx");
        await _repo.CreateMessageAsync(message);
        await SaveAsync();

        var (messages, _) = await _repo.GetMessagesByContextIdAsync("conv-ctx", take: 10, skip: 0);

        Assert.That(messages.Select(m => m.Id), Is.EquivalentTo(new[] { message.Id }));
    }

    [Test]
    public async Task GetMessagesByConversationIdAsync_RespectsTakeAndSkip()
    {
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var msg = MakeMessage(conversationId: "conv-page");
            await _repo.CreateMessageAsync(msg);
            await SaveAsync();
            ids.Add(msg.Id);
            await Task.Delay(2);
        }

        // Newest-first internally, then re-sorted ascending: skip=1,take=2 should return the
        // 2nd and 3rd most recent messages (indexes 3 and 2 in creation order), in ascending order.
        var (messages, _) = await _repo.GetMessagesByConversationIdAsync("conv-page", take: 2, skip: 1);
        var list = messages.ToList();

        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list[0].Id, Is.EqualTo(ids[2]));
        Assert.That(list[1].Id, Is.EqualTo(ids[3]));
    }
}
