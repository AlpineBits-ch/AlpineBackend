using Messaging.Application.Commands;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Commands;

/// <summary>
/// Covers UpdateMessageCommandHandler against a real EfCoreMessageRepository backed by the
/// EF Core InMemory provider - not-found, forbidden (non-author edit attempt), and success paths,
/// including that EmbedsJson is carried through untouched.
/// </summary>
[TestFixture]
public class UpdateMessageCommandTests
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

    private async Task<Message> SeedMessage(string authorId = "author-1", string? channelId = null, string? conversationId = "conv-1")
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "original"u8.ToArray(),
            ChannelId = channelId,
            ConversationId = conversationId,
            AuthorId = authorId,
        });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    [Test]
    public async Task Handle_MessageDoesNotExist_ReturnsNotFound()
    {
        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = "nope",
            RequestingAuthorId = "author-1",
            Content = "new content"u8.ToArray(),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.NotFound, Is.True);
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task Handle_RequestingUserIsNotAuthor_ReturnsForbidden()
    {
        var message = await SeedMessage(authorId: "author-1");

        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "someone-else",
            Content = "new content"u8.ToArray(),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Forbidden, Is.True);
            Assert.That(response.Success, Is.False);
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task Handle_ValidUpdate_PersistsContentAndReturnsEvent()
    {
        var message = await SeedMessage(authorId: "author-1", channelId: "chan-1", conversationId: null);

        var handler = new UpdateMessageCommandHandler();
        var newContent = "updated content"u8.ToArray();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = newContent,
        }, _repo);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Content, Is.EqualTo(newContent));
            Assert.That(response.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(response.AuthorId, Is.EqualTo("author-1"));
            Assert.That(response.UpdatedAt, Is.Not.Null);
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.MessageId, Is.EqualTo(message.Id));
            Assert.That(evt.Content, Is.EqualTo(newContent));
        });

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.That(stored!.Content, Is.EqualTo(newContent));
    }

    [Test]
    public async Task Handle_ValidUpdate_CarriesEmbedsJsonThrough()
    {
        var message = await SeedMessage(authorId: "author-1");

        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "hi"u8.ToArray(),
            EmbedsJson = "[{\"title\":\"card\"}]",
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
            Assert.That(evt!.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
        });
    }
}
