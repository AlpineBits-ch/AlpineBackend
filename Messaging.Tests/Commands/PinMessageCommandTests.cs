using Messaging.Application.Commands;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Commands;

/// <summary>
/// Covers PinMessageCommandHandler/UnpinMessageCommandHandler against a real
/// EfCoreMessageRepository backed by the EF Core InMemory provider - exercises the
/// not-found, already-pinned/already-unpinned idempotent, and success/event-emission paths.
/// </summary>
[TestFixture]
public class PinMessageCommandTests
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

    private static Message MakeMessage(string? channelId = null, string? conversationId = "conv-1") => Message.Create(new CreateMessageParams
    {
        Content = "hello"u8.ToArray(),
        ChannelId = channelId,
        ConversationId = conversationId,
        AuthorId = "author-1",
    });

    /// <summary>Seeds a message and detaches everything from the change tracker afterward,
    /// mirroring the fact that the handler under test runs against a fresh per-request DbContext
    /// in production - without this, GetMessageAsync's AsNoTracking() would materialize a
    /// *second* instance sharing the same key as the still-tracked seed instance, and the
    /// handler's later Update() call would throw EF's identity-conflict exception (a test-setup
    /// artifact, not a real handler bug).</summary>
    private async Task<Message> SeedMessage(string? channelId = null, string? conversationId = "conv-1")
    {
        var message = MakeMessage(channelId, conversationId);
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    private async Task PinDirectly(Message message, string pinnedById)
    {
        await _repo.PinMessageAsync(message, pinnedById);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PinMessageCommandHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Pin_MessageDoesNotExist_ReturnsNotFound()
    {
        var handler = new PinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new PinMessageCommand { MessageId = "nope", RequestingUserId = "user-1" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.NotFound, Is.True);
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task Pin_MessageNotYetPinned_PinsAndReturnsEvent()
    {
        var message = await SeedMessage();

        var handler = new PinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new PinMessageCommand { MessageId = message.Id, RequestingUserId = "user-1" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.ConversationId, Is.EqualTo("conv-1"));
            Assert.That(response.PinnedById, Is.EqualTo("user-1"));
            Assert.That(response.PinnedAt, Is.Not.Null);
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.MessageId, Is.EqualTo(message.Id));
            Assert.That(evt.PinnedById, Is.EqualTo("user-1"));
        });

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.That(stored!.IsPinned, Is.True);
    }

    [Test]
    public async Task Pin_MessageAlreadyPinned_IsIdempotent_NoEventEmitted()
    {
        var message = await SeedMessage();
        await PinDirectly(message, "original-pinner");

        var handler = new PinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new PinMessageCommand { MessageId = message.Id, RequestingUserId = "user-2" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.PinnedById, Is.EqualTo("original-pinner"), "Re-pinning must not overwrite who pinned it first");
            Assert.That(evt, Is.Null, "No new event should be raised for an already-pinned message");
        });
    }

    [Test]
    public async Task Pin_ChannelMessage_ResponseCarriesChannelId()
    {
        var message = await SeedMessage(channelId: "chan-1", conversationId: null);

        var handler = new PinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new PinMessageCommand { MessageId = message.Id, RequestingUserId = "user-1" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(response.ConversationId, Is.Null);
            Assert.That(evt!.ChannelId, Is.EqualTo("chan-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UnpinMessageCommandHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Unpin_MessageDoesNotExist_ReturnsNotFound()
    {
        var handler = new UnpinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UnpinMessageCommand { MessageId = "nope", RequestingUserId = "user-1" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.NotFound, Is.True);
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task Unpin_MessageNotPinned_IsIdempotent_NoEventEmitted()
    {
        var message = await SeedMessage();

        var handler = new UnpinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UnpinMessageCommand { MessageId = message.Id, RequestingUserId = "user-1" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task Unpin_PinnedMessage_UnpinsAndReturnsEvent()
    {
        var message = await SeedMessage();
        await PinDirectly(message, "pinner-1");

        var handler = new UnpinMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UnpinMessageCommand { MessageId = message.Id, RequestingUserId = "user-2" }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.UnpinnedById, Is.EqualTo("user-2"));
        });

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.IsPinned, Is.False);
            Assert.That(stored.PinnedAt, Is.Null);
            Assert.That(stored.PinnedById, Is.Null);
        });
    }
}
