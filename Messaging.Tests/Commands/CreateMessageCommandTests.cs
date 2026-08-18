using Messaging.Application.Commands;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using ContractMessageEncryptionState = Messaging.Contracts.Bus.Commands.MessageEncryptionState;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;
using ContractAuthorIdType = Messaging.Contracts.Bus.Commands.AuthorIdType;
using DomainAuthorIdType = Messaging.Domain.Enums.AuthorIdType;

namespace Messaging.Tests.Commands;

/// <summary>
/// Covers CreateMessageCommandHandler: the message is always persisted via IMessageRepository,
/// but the MessageSearchEntry side effect on MicroserviceContext is conditional - only
/// Plain-encryption, ordinary (Type=Message), non-empty-content messages get indexed (nothing to
/// search in an MLS ciphertext blob, and system messages carry no user-authored content).
/// </summary>
[TestFixture]
public class CreateMessageCommandTests
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

    private static global::Messaging.Contracts.Bus.Commands.CreateMessageCommand MakeCommand(
        ContractMessageEncryptionState encryption = ContractMessageEncryptionState.Plain,
        ContractMessageType type = ContractMessageType.Message,
        string content = "hello world") => new()
    {
        AuthorId = "author-1",
        Content = System.Text.Encoding.UTF8.GetBytes(content),
        ConversationId = "conv-1",
        EncryptionState = encryption,
        Type = type,
    };

    [Test]
    public async Task Handle_PersistsMessageViaRepository()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, _) = await handler.Handle(MakeCommand(), _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.That(stored, Is.Not.Null);
    }

    [Test]
    public async Task Handle_PlainEncryptionOrdinaryMessage_CreatesSearchEntry()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, _) = await handler.Handle(MakeCommand(), _repo, _context);

        // The handler itself never calls db.SaveChangesAsync() on the MessageSearchEntry it adds
        // (correctly, per the Wolverine convention - this Handle method is bus-dispatched and
        // auto-wrapped, so Wolverine's middleware commits it after Handle returns).
        await _context.SaveChangesAsync();

        var entry = _context.MessageSearchEntries.FirstOrDefault(e => e.MessageId == message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.Content, Is.EqualTo("hello world"));
            Assert.That(entry.ConversationId, Is.EqualTo("conv-1"));
            Assert.That(entry.AuthorId, Is.EqualTo("author-1"));
        });
    }

    /// <summary>The event carries the timestamp the row was actually stored under, not one taken
    /// again when the event is built. Downstream, Guild denormalizes it onto the channel row and
    /// compares it against read cursors, so the two have to be the same instant.</summary>
    [Test]
    public async Task Handle_StampsEventCreatedAtFromTheStoredMessage()
    {
        var handler = new CreateMessageCommandHandler();

        var (message, evt) = await handler.Handle(MakeCommand(), _repo, _context);

        Assert.That(evt.CreatedAt, Is.EqualTo(message.CreatedAt));
    }

    [Test]
    public async Task Handle_EncryptedMessage_DoesNotCreateSearchEntry()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, _) = await handler.Handle(MakeCommand(encryption: ContractMessageEncryptionState.Encrypted), _repo, _context);

        Assert.That(_context.MessageSearchEntries.Any(e => e.MessageId == message.Id), Is.False);
    }

    [Test]
    public async Task Handle_SystemMessageType_DoesNotCreateSearchEntry()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, _) = await handler.Handle(MakeCommand(type: ContractMessageType.GuildMemberJoin, content: ""), _repo, _context);

        Assert.That(_context.MessageSearchEntries.Any(e => e.MessageId == message.Id), Is.False);
    }

    [Test]
    public async Task Handle_EmptyContent_DoesNotCreateSearchEntry()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, _) = await handler.Handle(MakeCommand(content: ""), _repo, _context);

        Assert.That(_context.MessageSearchEntries.Any(e => e.MessageId == message.Id), Is.False);
    }

    [Test]
    public async Task Handle_ReturnsMessageCreatedEvent_WithMatchingFields()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        var (message, evt) = await handler.Handle(command, _repo, _context);

        Assert.Multiple(() =>
        {
            Assert.That(evt.MessageId, Is.EqualTo(message.Id));
            Assert.That(evt.ConversationId, Is.EqualTo("conv-1"));
            Assert.That(evt.AuthorId, Is.EqualTo("author-1"));
            Assert.That(evt.EncryptionState, Is.EqualTo(global::Messaging.Domain.Enums.MessageEncryptionState.Plain));
        });
    }

    /// <summary>
    /// Realtime clients render an image attachment straight off the event's ThumbnailUrl; there is
    /// no DTO between the event and the SignalR frame. An empty one leaves the image blank until a
    /// reload refetches the message over REST.
    /// </summary>
    [Test]
    public async Task Handle_EventAttachmentsCarryTheThumbnail()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.Attachments.Add(new global::Messaging.Contracts.Bus.Commands.MinimalAttachmentContract
        {
            Id = "atac-1",
            FileName = "image.png",
            ContentType = "image/png",
            ThumbnailUrl = "https://api.venta.gg/api/v1/messaging/attachments/atac-1/thumbnail",
            ThumbnailId = "thumbs/atac-1.webp",
        });

        var (message, evt) = await handler.Handle(command, _repo, _context);

        var attachment = evt.Attachments.Single();
        Assert.Multiple(() =>
        {
            Assert.That(attachment.Id, Is.EqualTo("atac-1"));
            Assert.That(attachment.FileName, Is.EqualTo("image.png"));
            Assert.That(attachment.ContentType, Is.EqualTo("image/png"));
            Assert.That(attachment.ThumbnailUrl, Is.EqualTo("https://api.venta.gg/api/v1/messaging/attachments/atac-1/thumbnail"));
            Assert.That(attachment.ThumbnailId, Is.EqualTo("thumbs/atac-1.webp"));
            Assert.That(attachment.CreatedAt, Is.EqualTo(message.Attachments.Single().CreatedAt));
        });
    }

    [Test]
    public async Task Handle_InviteType_MapsToDomainInviteType()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, evt) = await handler.Handle(MakeCommand(type: ContractMessageType.Invite), _repo, _context);

        Assert.Multiple(() =>
        {
            Assert.That(message.Type, Is.EqualTo(global::Messaging.Domain.Enums.MessageType.Invite));
            Assert.That(evt.Type, Is.EqualTo(global::Messaging.Domain.Enums.MessageType.Invite));
        });
    }

    [Test]
    public async Task Handle_BotAuthor_StoresBotAuthorIdType()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.AuthorIdType = ContractAuthorIdType.Bot;

        var (message, evt) = await handler.Handle(command, _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Bot));
            Assert.That(evt.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Bot));
        });
    }

    /// <summary>
    /// The regression this mapping exists for: a webhook execution sets Webhook plus the display
    /// overrides, and the command handler used to drop the type on the floor, so every webhook
    /// message was persisted claiming a real user authored it.
    /// </summary>
    [Test]
    public async Task Handle_WebhookExecution_KeepsWebhookTypeAndDisplayOverrides()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.AuthorIdType = ContractAuthorIdType.Webhook;
        command.AuthorDisplayName = "Deploy Bot";
        command.AuthorAvatarUrl = "https://api.venta.gg/avatars/deploy.png";

        var (message, evt) = await handler.Handle(command, _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Webhook));
            Assert.That(stored.AuthorDisplayName, Is.EqualTo("Deploy Bot"));
            Assert.That(stored.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/deploy.png"));
            Assert.That(evt.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Webhook));
            Assert.That(evt.AuthorDisplayName, Is.EqualTo("Deploy Bot"));
        });
    }

    [Test]
    public async Task Handle_UnsetAuthorIdType_DefaultsToUser()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, evt) = await handler.Handle(MakeCommand(), _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.AuthorIdType, Is.EqualTo(DomainAuthorIdType.User));
            Assert.That(evt.AuthorIdType, Is.EqualTo(DomainAuthorIdType.User));
        });
    }

    /// <summary>
    /// An encrypted message stored without its generation cannot be matched back to the MLS group it
    /// was sealed to, so this was dropped alongside AuthorIdType.
    /// </summary>
    [Test]
    public async Task Handle_CarriesMlsGenerationToStorage()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand(encryption: ContractMessageEncryptionState.Encrypted);
        command.MlsGeneration = 7;

        var (message, evt) = await handler.Handle(command, _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.MlsGeneration, Is.EqualTo(7));
            Assert.That(evt.MlsGeneration, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task Handle_PersonaMessage_StoresTheCharacterAndLeavesAuthorIdAlone()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.AuthorIdType = ContractAuthorIdType.Persona;
        command.PersonaId = "pers_cogsgrove";
        command.AuthorDisplayName = "Mayor Cogsgrove";
        command.AuthorAvatarUrl = "https://api.venta.gg/avatars/cogsgrove.png";

        var (message, evt) = await handler.Handle(command, _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.PersonaId, Is.EqualTo("pers_cogsgrove"));
            Assert.That(stored.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Persona));
            Assert.That(stored.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(stored.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/cogsgrove.png"));

            // The whole point of the design: blocking, moderation and reply-pings resolve against
            // the real account, so a costume must never reach AuthorId.
            Assert.That(stored.AuthorId, Is.EqualTo("author-1"));
            Assert.That(evt.AuthorId, Is.EqualTo("author-1"));
            Assert.That(evt.PersonaId, Is.EqualTo("pers_cogsgrove"));
        });
    }

    /// <summary>
    /// MessageCreatedHandler pushes under the display override when there is one; an event that
    /// carried only the account name is what leaked who plays the character.
    /// </summary>
    [Test]
    public async Task Handle_PersonaMessage_EventCarriesTheDisplayOverridesForPush()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.AuthorIdType = ContractAuthorIdType.Persona;
        command.PersonaId = "pers_cogsgrove";
        command.AuthorDisplayName = "Mayor Cogsgrove";
        command.AuthorAvatarUrl = "https://api.venta.gg/avatars/cogsgrove.png";

        var (_, evt) = await handler.Handle(command, _repo, _context);

        Assert.Multiple(() =>
        {
            Assert.That(evt.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(evt.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/cogsgrove.png"));
            Assert.That(evt.AuthorIdType, Is.EqualTo(DomainAuthorIdType.Persona));
        });
    }

    [Test]
    public async Task Handle_OrdinaryMessage_HasNoPersona()
    {
        var handler = new CreateMessageCommandHandler();
        var (message, evt) = await handler.Handle(MakeCommand(), _repo, _context);
        await _context.SaveChangesAsync();

        var stored = await _repo.GetMessageAsync(message.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.PersonaId, Is.Null);
            Assert.That(stored.AuthorDisplayName, Is.Null);
            Assert.That(evt.PersonaId, Is.Null);
        });
    }

    /// <summary>A persona message is still the user's message, so it is indexed like any other.</summary>
    [Test]
    public async Task Handle_PersonaMessage_IndexesUnderTheRealAuthor()
    {
        var handler = new CreateMessageCommandHandler();
        var command = MakeCommand();
        command.AuthorIdType = ContractAuthorIdType.Persona;
        command.PersonaId = "pers_cogsgrove";
        command.AuthorDisplayName = "Mayor Cogsgrove";

        var (message, _) = await handler.Handle(command, _repo, _context);
        await _context.SaveChangesAsync();

        var entry = _context.MessageSearchEntries.FirstOrDefault(e => e.MessageId == message.Id);
        Assert.That(entry!.AuthorId, Is.EqualTo("author-1"));
    }
}
