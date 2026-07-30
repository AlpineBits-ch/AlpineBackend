using Messaging.Application.Commands;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using ContractMessageEncryptionState = Messaging.Contracts.Bus.Commands.MessageEncryptionState;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;

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
        // auto-wrapped, so Wolverine's middleware commits it after Handle returns). Calling it
        // here simulates that middleware for the purposes of asserting the entry was persisted.
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
}
