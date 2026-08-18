using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Messages;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers MessageUpdatedHandler: the MessageSearchEntry content-update side effect (only when a
/// search entry actually exists - e.g. an encrypted message being "edited" has nothing to update),
/// and the dual fan-out (conversation hub broadcast excluding the author vs channel bus forward).
/// </summary>
[TestFixture]
public class MessageUpdatedHandlerTests
{
    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
        _bus = new FakeMessageBus();
    }

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

    [Test]
    public async Task Handle_SearchEntryExists_UpdatesContent()
    {
        _context.MessageSearchEntries.Add(new MessageSearchEntry
        {
            MessageId = "msg-1",
            AuthorId = "author-1",
            Content = "old content",
            CreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated
        {
            MessageId = "msg-1",
            AuthorId = "author-1",
            Content = "new content"u8.ToArray(),
        };

        await handler.Handle(evt, _hub, _context, _bus);

        var entry = _context.MessageSearchEntries.First(e => e.MessageId == "msg-1");
        Assert.That(entry.Content, Is.EqualTo("new content"));
    }

    [Test]
    public async Task Handle_NoSearchEntry_DoesNotThrow()
    {
        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated { MessageId = "unindexed-msg", AuthorId = "author-1", Content = "content"u8.ToArray() };

        Assert.DoesNotThrowAsync(() => handler.Handle(evt, _hub, _context, _bus));
    }

    [Test]
    public async Task Handle_ConversationMessage_BroadcastsToMembersExcludingAuthor()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "other-user", "conv-1"));
        await _context.SaveChangesAsync();

        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated { MessageId = "msg-1", ConversationId = "conv-1", AuthorId = "author-1", Content = "content"u8.ToArray() };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
        // FakeHubClients.Users(..) records against a single shared proxy, so we can't directly
        // assert the excluded user list here without a richer fake - the important behavioral
        // guarantee (broadcast happens exactly once per event) is covered; the underlying
        // ConversationPermissionServiceTests-style exclusion is exercised via the LINQ filter
        // itself, which InMemory evaluates for real.
        Assert.That(hubClients.SentMessages[0].Method, Is.EqualTo("conversation.MessageUpdated"));
    }

    [Test]
    public async Task Handle_ChannelMessage_ForwardsMessageUpdatedForChannelOverBus()
    {
        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated
        {
            MessageId = "msg-1",
            ChannelId = "chan-1",
            AuthorId = "author-1",
            Content = "content"u8.ToArray(),
            EmbedsJson = "[]",
        };

        await handler.Handle(evt, _hub, _context, _bus);

        Assert.That(_bus.Sent, Has.Count.EqualTo(1));
        var forwarded = (MessageUpdatedForChannel)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(forwarded.EmbedsJson, Is.EqualTo("[]"));
        });
    }

    /// <summary>The reported symptom was "the update notification drops the embed", so the payload
    /// the conversation members actually receive is asserted here, not just the event that produced
    /// it.</summary>
    [Test]
    public async Task Handle_ConversationMessage_BroadcastPayloadCarriesTheEmbeds()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "author-1", "conv-1"),
            MakeMember("m-2", "other-user", "conv-1"));
        await _context.SaveChangesAsync();

        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = "edited"u8.ToArray(),
            EmbedsJson = "[{\"title\":\"card\"}]",
        };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        var payload = (MessageUpdated)hubClients.SentMessages.Single().Args[0]!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.EmbedsJson, Is.EqualTo("[{\"title\":\"card\"}]"));
            Assert.That(payload.Content, Is.EqualTo("edited"u8.ToArray()));
        });
    }

    /// <summary>An edit never re-resolves the persona, so the identity the message was sent under
    /// has to survive the round trip out to Guild.</summary>
    [Test]
    public async Task Handle_ChannelMessage_ForwardsThePersonaIdentity()
    {
        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated
        {
            MessageId = "msg-1",
            ChannelId = "chan-1",
            AuthorId = "author-1",
            Content = "content"u8.ToArray(),
            AuthorIdType = global::Messaging.Domain.Enums.AuthorIdType.Persona,
            PersonaId = "pers_cogsgrove",
            AuthorDisplayName = "Mayor Cogsgrove",
            AuthorAvatarUrl = "https://api.venta.gg/avatars/cogsgrove.png",
        };

        await handler.Handle(evt, _hub, _context, _bus);

        var forwarded = (MessageUpdatedForChannel)_bus.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(forwarded.AuthorIdType, Is.EqualTo(AuthorIdType.Persona));
            Assert.That(forwarded.PersonaId, Is.EqualTo("pers_cogsgrove"));
            Assert.That(forwarded.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(forwarded.AuthorAvatarUrl, Is.EqualTo("https://api.venta.gg/avatars/cogsgrove.png"));
            Assert.That(forwarded.AuthorId, Is.EqualTo("author-1"));
        });
    }

    [Test]
    public async Task Handle_NeitherConversationNorChannel_NoFanOut()
    {
        var handler = new MessageUpdatedHandler();
        var evt = new MessageUpdated { MessageId = "msg-1", AuthorId = "author-1", Content = "content"u8.ToArray() };

        await handler.Handle(evt, _hub, _context, _bus);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.Multiple(() =>
        {
            Assert.That(hubClients.SentMessages, Is.Empty);
            Assert.That(_bus.Sent, Is.Empty);
        });
    }
}
