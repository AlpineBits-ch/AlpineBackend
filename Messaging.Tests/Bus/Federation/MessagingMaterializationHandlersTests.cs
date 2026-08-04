using Federation.Contracts.Materialization.Messaging;
using Messaging.Application.Bus.Federation;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Bus.Federation;

/// <summary>
/// Covers MessagingMaterializationHandlers: materializes remote channel-message federation events
/// via IMessageRepository and re-publishes the same local domain events a normal message
/// create/edit/delete raises, idempotent via GetMessageAsync-before-write rather than EventId.
/// </summary>
[TestFixture]
public class MessagingMaterializationHandlersTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Message> SeedMessage(string id, string channelId = "chan-1", string authorId = "author-1",
        string? embedsJson = null)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "original"u8.ToArray(),
            ChannelId = channelId,
            AuthorId = authorId,
            EmbedsJson = embedsJson,
        });
        message.Id = id;
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedMessageCreatedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MessageCreated_NewMessage_PersistsAndPublishesEvent()
    {
        var message = new FederatedMessageCreatedReceived
        {
            EventId = "evt-1",
            OriginInstanceId = "instance-a",
            SenderId = "user-1:instance-a",
            ChannelId = "chan-1",
            MessageId = "fed-msg-1",
            Content = "hello"u8.ToArray(),
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync("fed-msg-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.AuthorId, Is.EqualTo("user-1:instance-a"));
            Assert.That(_bus.Published, Has.Count.EqualTo(1));
            Assert.That(_bus.Published[0], Is.InstanceOf<MessageCreated>());
        });
    }

    [Test]
    public async Task MessageCreated_AlreadyExists_IsIdempotent_DoesNotDuplicateOrPublish()
    {
        await SeedMessage("fed-msg-1");

        var message = new FederatedMessageCreatedReceived
        {
            EventId = "evt-2",
            OriginInstanceId = "instance-a",
            SenderId = "user-1:instance-a",
            ChannelId = "chan-1",
            MessageId = "fed-msg-1",
            Content = "duplicate delivery"u8.ToArray(),
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty, "Re-delivery of an already-materialized message must not re-publish");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedMessageEditedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MessageEdited_ExistingMessage_UpdatesContentAndPublishesEvent()
    {
        await SeedMessage("fed-msg-1");

        var message = new FederatedMessageEditedReceived
        {
            EventId = "evt-3",
            OriginInstanceId = "instance-a",
            SenderId = "author-1",
            ChannelId = "chan-1",
            MessageId = "fed-msg-1",
            Content = "edited content"u8.ToArray(),
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync("fed-msg-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Content, Is.EqualTo("edited content"u8.ToArray()));
            Assert.That(_bus.Published, Has.Count.EqualTo(1));
            Assert.That(_bus.Published[0], Is.InstanceOf<MessageUpdated>());
        });
    }

    /// <summary>A federated edit only ever carries text, and it does not touch the stored embeds -
    /// so the update event it publishes has to read them off the row. Omitting them told every
    /// client that an edited message had lost its card when nothing of the sort had happened.</summary>
    [Test]
    public async Task MessageEdited_PublishedEventCarriesTheStoredEmbeds()
    {
        await SeedMessage("fed-msg-1", embedsJson: """[{"title":"card"}]""");

        var message = new FederatedMessageEditedReceived
        {
            EventId = "evt-3b",
            OriginInstanceId = "instance-a",
            SenderId = "author-1",
            ChannelId = "chan-1",
            MessageId = "fed-msg-1",
            Content = "edited content"u8.ToArray(),
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);
        await _context.SaveChangesAsync();

        var published = (MessageUpdated)_bus.Published.Single();
        var stored = await _repo.GetMessageAsync("fed-msg-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored!.EmbedsJson, Is.EqualTo("""[{"title":"card"}]"""));
            Assert.That(published.EmbedsJson, Is.EqualTo("""[{"title":"card"}]"""));
        });
    }

    [Test]
    public async Task MessageEdited_MessageDoesNotExist_IsNoOp()
    {
        var message = new FederatedMessageEditedReceived
        {
            EventId = "evt-4",
            OriginInstanceId = "instance-a",
            SenderId = "author-1",
            ChannelId = "chan-1",
            MessageId = "nonexistent",
            Content = "edited"u8.ToArray(),
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FederatedMessageDeletedReceived
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MessageDeleted_ExistingMessage_RemovesAndPublishesEvent()
    {
        await SeedMessage("fed-msg-1");

        var message = new FederatedMessageDeletedReceived
        {
            EventId = "evt-5",
            OriginInstanceId = "instance-a",
            SenderId = "author-1",
            ChannelId = "chan-1",
            MessageId = "fed-msg-1",
        };

        await MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Messages.Any(m => m.Id == "fed-msg-1"), Is.False);
            Assert.That(_bus.Published, Has.Count.EqualTo(1));
            Assert.That(_bus.Published[0], Is.InstanceOf<MessageDeleted>());
        });
    }

    [Test]
    public async Task MessageDeleted_MessageDoesNotExist_IsNoOp()
    {
        var message = new FederatedMessageDeletedReceived
        {
            EventId = "evt-6",
            OriginInstanceId = "instance-a",
            SenderId = "author-1",
            ChannelId = "chan-1",
            MessageId = "nonexistent",
        };

        Assert.DoesNotThrowAsync(() => MessagingMaterializationHandlers.Handle(message, _repo, _bus, CancellationToken.None));
    }
}
