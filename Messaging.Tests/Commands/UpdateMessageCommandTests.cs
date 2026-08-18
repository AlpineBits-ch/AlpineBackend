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

    private async Task<Message> SeedMessage(string authorId = "author-1", string? channelId = null,
        string? conversationId = "conv-1", string? embedsJson = null, string? componentsJson = null)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "original"u8.ToArray(),
            ChannelId = channelId,
            ConversationId = conversationId,
            AuthorId = authorId,
            EmbedsJson = embedsJson,
        });
        message.ComponentsJson = componentsJson;
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

    /// <summary>Editing a persona message keeps the identity it was sent under - the handler reads
    /// it back off the row rather than re-resolving anything.</summary>
    [Test]
    public async Task Handle_PersonaMessage_EventKeepsTheSentIdentity()
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "original"u8.ToArray(),
            ChannelId = "chan-1",
            AuthorId = "author-1",
            AuthorIdType = global::Messaging.Domain.Enums.AuthorIdType.Persona,
            PersonaId = "pers_cogsgrove",
            AuthorDisplayName = "Mayor Cogsgrove",
            AuthorAvatarUrl = "https://api.venta.gg/avatars/cogsgrove.png",
        });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var handler = new UpdateMessageCommandHandler();
        var (_, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "edited"u8.ToArray(),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(evt!.AuthorIdType, Is.EqualTo(global::Messaging.Domain.Enums.AuthorIdType.Persona));
            Assert.That(evt.PersonaId, Is.EqualTo("pers_cogsgrove"));
            Assert.That(evt.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(evt.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/cogsgrove.png"));
            Assert.That(evt.AuthorId, Is.EqualTo("author-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Patch semantics: null
    // leaves the field alone, an empty array clears it

    [Test]
    public async Task Handle_ContentOnlyEdit_LeavesTheStoredEmbedIntact()
    {
        var message = await SeedMessage(authorId: "author-1", channelId: "chan-1", conversationId: null,
            embedsJson: "[{\"title\":\"card\"}]");

        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "edited text"u8.ToArray(),
            // No embeds: the caller is saying nothing about them.
        }, _repo);
        await _context.SaveChangesAsync();

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"), "the embed must survive in storage");
            Assert.That(stored.Content, Is.EqualTo("edited text"u8.ToArray()));
            Assert.That(response.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
            Assert.That(evt!.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"),
                "and the update notification must carry it - this is the reported symptom");
        });
    }

    [Test]
    public async Task Handle_ExplicitEmptyEmbedsArray_ClearsTheEmbed()
    {
        var message = await SeedMessage(authorId: "author-1", embedsJson: "[{\"title\":\"card\"}]");

        var handler = new UpdateMessageCommandHandler();
        var (_, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "no card any more"u8.ToArray(),
            EmbedsJson = "[]",
        }, _repo);
        await _context.SaveChangesAsync();

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.EmbedsJson, Is.EqualTo("[]"));
            Assert.That(evt!.EmbedsJson, Is.EqualTo("[]"));
        });
    }

    /// <summary>An embeds-only or components-only edit sends no content, which must not blank the
    /// message - the same asymmetry, one field over.</summary>
    [Test]
    public async Task Handle_NullContent_LeavesTheStoredContentAlone()
    {
        var message = await SeedMessage(authorId: "author-1");

        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = null,
            EmbedsJson = "[{\"title\":\"card\"}]",
        }, _repo);
        await _context.SaveChangesAsync();

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Content, Is.EqualTo("original"u8.ToArray()));
            Assert.That(stored.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
            Assert.That(response.Content, Is.EqualTo("original"u8.ToArray()));
            Assert.That(evt!.Content, Is.EqualTo("original"u8.ToArray()));
        });
    }

    /// <summary>Components already had these semantics; pinned here so the two fields cannot drift
    /// apart again.</summary>
    [Test]
    public async Task Handle_ContentOnlyEdit_LeavesTheStoredComponentsIntact()
    {
        var message = await SeedMessage(authorId: "author-1", componentsJson: "[{\"type\":1}]");

        var handler = new UpdateMessageCommandHandler();
        await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "edited"u8.ToArray(),
        }, _repo);
        await _context.SaveChangesAsync();

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.That(stored!.ComponentsJson, Is.EqualTo("[{\"type\":1}]"));
    }

    /// <summary>The negative case still holds with the patch semantics in place: a non-author edit
    /// is refused before anything is written, embeds included.</summary>
    [Test]
    public async Task Handle_NonAuthorEdit_ChangesNothingAtAll()
    {
        var message = await SeedMessage(authorId: "author-1", embedsJson: "[{\"title\":\"card\"}]");

        var handler = new UpdateMessageCommandHandler();
        var (response, evt) = await handler.Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "someone-else",
            Content = "hijacked"u8.ToArray(),
            EmbedsJson = "[]",
        }, _repo);
        await _context.SaveChangesAsync();

        var stored = await _context.Messages.FindAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(response.Forbidden, Is.True);
            Assert.That(evt, Is.Null);
            Assert.That(stored!.Content, Is.EqualTo("original"u8.ToArray()));
            Assert.That(stored.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
        });
    }
}
