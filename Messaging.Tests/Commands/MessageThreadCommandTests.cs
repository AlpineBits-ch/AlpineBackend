using Messaging.Application.Commands;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Commands;

/// <summary>
/// Covers AttachThreadToMessageCommandHandler/DetachThreadFromMessageCommandHandler against a real
/// EfCoreMessageRepository on the InMemory provider: the three refusals Guild maps onto 404/409,
/// the at-least-once replay, and the id-guarded detach.
/// </summary>
[TestFixture]
public class MessageThreadCommandTests
{
    private const string ChannelId = "chan-1";

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

    /// <summary>Seeds a message and clears the change tracker, so the handler's AsNoTracking read
    /// does not collide with the still-tracked seed instance - see PinMessageCommandTests.</summary>
    private async Task<Message> SeedMessage(string? channelId = ChannelId, string? conversationId = null)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hello"u8.ToArray(),
            ChannelId = channelId,
            ConversationId = conversationId,
            AuthorId = "author-1",
        });

        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    private async Task<AttachThreadToMessageResponse> Attach(string messageId, string threadId, string channelId = ChannelId)
    {
        var response = await new AttachThreadToMessageCommandHandler().Handle(
            new AttachThreadToMessageCommand { MessageId = messageId, ChannelId = channelId, ThreadId = threadId },
            _repo, _context);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return response;
    }

    private async Task Detach(string messageId, string threadId)
    {
        await new DetachThreadFromMessageCommandHandler().Handle(
            new DetachThreadFromMessageCommand { MessageId = messageId, ThreadId = threadId }, _repo, _context);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<string?> ThreadIdOf(string messageId) =>
        (await _repo.GetMessageAsync(messageId))!.ThreadId;

    // ══════════════════════════════════════════════════════════════════════════ Attach
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Attach_UnknownMessage_ReturnsMessageNotFound()
    {
        var response = await Attach("mesg-missing", "chan-thread");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.MessageNotFound));
    }

    [Test]
    public async Task Attach_MessageInAnotherChannel_ReturnsWrongChannel()
    {
        var message = await SeedMessage(channelId: "chan-elsewhere");

        var response = await Attach(message.Id, "chan-thread");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.WrongChannel));
        Assert.That(await ThreadIdOf(message.Id), Is.Null);
    }

    [Test]
    public async Task Attach_ConversationMessage_ReturnsWrongChannel()
    {
        // A DM has no parent channel to hang a thread under, and its ChannelId is null.
        var message = await SeedMessage(channelId: null, conversationId: "conv-1");

        var response = await Attach(message.Id, "chan-thread");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.WrongChannel));
    }

    [Test]
    public async Task Attach_Valid_StoresTheThreadId()
    {
        var message = await SeedMessage();

        var response = await Attach(message.Id, "chan-thread");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.Attached));
        Assert.That(await ThreadIdOf(message.Id), Is.EqualTo("chan-thread"));
    }

    [Test]
    public async Task Attach_SameThreadTwice_IsIdempotent()
    {
        // Wolverine's inbox is at-least-once, so a redelivery must not read as a conflict.
        var message = await SeedMessage();
        await Attach(message.Id, "chan-thread");

        var response = await Attach(message.Id, "chan-thread");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.Attached));
    }

    [Test]
    public async Task Attach_DifferentThread_ReturnsAlreadyHasThreadAndKeepsTheFirst()
    {
        var message = await SeedMessage();
        await Attach(message.Id, "chan-first");

        var response = await Attach(message.Id, "chan-second");

        Assert.That(response.Outcome, Is.EqualTo(AttachThreadOutcome.AlreadyHasThread));
        Assert.That(response.ExistingThreadId, Is.EqualTo("chan-first"));
        Assert.That(await ThreadIdOf(message.Id), Is.EqualTo("chan-first"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Detach
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Detach_ClearsThePointer()
    {
        var message = await SeedMessage();
        await Attach(message.Id, "chan-thread");

        await Detach(message.Id, "chan-thread");

        Assert.That(await ThreadIdOf(message.Id), Is.Null);
    }

    [Test]
    public async Task Detach_NamingAnOlderThread_LeavesTheCurrentOne()
    {
        // A detach that lost a race against a re-create must not unlink the replacement.
        var message = await SeedMessage();
        await Attach(message.Id, "chan-current");

        await Detach(message.Id, "chan-stale");

        Assert.That(await ThreadIdOf(message.Id), Is.EqualTo("chan-current"));
    }

    [Test]
    public void Detach_UnknownMessage_DoesNothing()
    {
        Assert.DoesNotThrowAsync(() => Detach("mesg-missing", "chan-thread"));
    }
}
