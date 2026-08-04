using Bots.Contracts.Gateway.Payloads;
using Messaging.Application.Handler.Messages;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;

namespace Messaging.Tests.Handlers;

/// <summary>
/// The gate that decides whether a message is a candidate for link previews at all.
/// </summary>
[TestFixture]
public class UnfurlLinksHandlerTests
{
    private static MessageCreated Created(string content, Action<MessageCreated>? customise = null)
    {
        var evt = new MessageCreated
        {
            MessageId = "msg-1",
            ContextId = "conv-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            Attachments = [],
        };
        customise?.Invoke(evt);
        return evt;
    }

    // ── Create path ──────────────────────────────────────────────────────────

    [Test]
    public void PlainMessageWithALink_IsQueued()
    {
        var queued = UnfurlLinksHandler.Handle(Created("look at https://example.com"));

        Assert.That(queued, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(queued!.MessageId, Is.EqualTo("msg-1"));
            Assert.That(queued.ContextId, Is.EqualTo("conv-1"));
        });
    }

    [Test]
    public void MessageWithoutALink_IsNotQueued()
    {
        Assert.That(UnfurlLinksHandler.Handle(Created("just talking")), Is.Null);
    }

    [Test]
    public void EncryptedMessage_IsNeverQueued()
    {
        // Content is MLS ciphertext - the server cannot see a URL, and this is a property of E2EE
        // rather than a gap to work around. Same wall that stops search indexing.
        var evt = Created("https://example.com", e => e.EncryptionState = MessageEncryptionState.Encrypted);

        Assert.That(UnfurlLinksHandler.Handle(evt), Is.Null);
    }

    [Test]
    public void SystemMessage_IsNeverQueued()
    {
        var evt = Created("https://example.com", e => e.Type = MessageType.GuildMemberJoin);

        Assert.That(UnfurlLinksHandler.Handle(evt), Is.Null);
    }

    [Test]
    public void MessageCarryingAuthorEmbeds_IsNotQueued()
    {
        // Discord does not add link previews on top of a bot's own cards, and there is no sensible
        // way to order the two.
        var authored = GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]);
        var evt = Created("https://example.com", e => e.EmbedsJson = authored);

        Assert.That(UnfurlLinksHandler.Handle(evt), Is.Null);
    }

    [Test]
    public void MessageCarryingOnlyGeneratedEmbeds_IsStillQueued()
    {
        // Previously-generated previews are ours to replace - only author content stands us down.
        var generated = GeneratedEmbeds.Merge(null, [new EmbedPayload { Title = "old preview" }]);
        var evt = Created("https://example.com", e => e.EmbedsJson = generated);

        Assert.That(UnfurlLinksHandler.Handle(evt), Is.Not.Null);
    }

    [Test]
    public void LinkInsideACodeFence_IsNotQueued()
    {
        Assert.That(UnfurlLinksHandler.Handle(Created("```\ncurl https://example.com\n```")), Is.Null);
    }

    // ── Edit path, and the loop it could cause ───────────────────────────────

    [Test]
    public void AuthorEditWithALink_IsRequeued()
    {
        var queued = UnfurlLinksHandler.Handle(new MessageUpdated
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = "now see https://example.com/other"u8.ToArray(),
            IsAuthorEdit = true,
        });

        Assert.That(queued, Is.Not.Null);
    }

    [Test]
    public void TheUnfurlersOwnUpdate_IsNotRequeued()
    {
        // THE loop guard.
        var queued = UnfurlLinksHandler.Handle(new MessageUpdated
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = "see https://example.com"u8.ToArray(),
            IsAuthorEdit = false,
        });

        Assert.That(queued, Is.Null);
    }

    [Test]
    public void SuppressedMessage_IsNotRequeuedOnEdit()
    {
        var queued = UnfurlLinksHandler.Handle(new MessageUpdated
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = "see https://example.com"u8.ToArray(),
            IsAuthorEdit = true,
            Flags = MessageFlags.SuppressEmbeds,
        });

        Assert.That(queued, Is.Null);
    }

    [Test]
    public void ChannelEdit_ResolvesTheContextFromTheChannelId()
    {
        var queued = UnfurlLinksHandler.Handle(new MessageUpdated
        {
            MessageId = "msg-1",
            ChannelId = "chan-9",
            AuthorId = "author-1",
            Content = "see https://example.com"u8.ToArray(),
            IsAuthorEdit = true,
        });

        Assert.That(queued!.ContextId, Is.EqualTo("chan-9"));
    }

    [Test]
    public void EditWithNoContext_IsNotQueued()
    {
        var queued = UnfurlLinksHandler.Handle(new MessageUpdated
        {
            MessageId = "msg-1",
            AuthorId = "author-1",
            Content = "see https://example.com"u8.ToArray(),
            IsAuthorEdit = true,
        });

        Assert.That(queued, Is.Null);
    }
}
