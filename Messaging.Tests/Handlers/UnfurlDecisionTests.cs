using Bots.Contracts.Gateway.Payloads;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;

namespace Messaging.Tests.Handlers;

/// <summary>
/// The gate that decides whether a message gets link previews. It runs on every message posted on
/// the instance, and a false positive turns into an outbound HTTP request from the server - so the
/// negative cases here matter at least as much as the positive ones.
///
/// <para>Tests the predicate rather than a handler, because that is now where the decision lives:
/// subscribing to MessageCreated from a second handler class silently never ran. See
/// <see cref="UnfurlDecision"/>.</para>
/// </summary>
[TestFixture]
public class UnfurlDecisionTests
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

    /// <summary>Mirrors the call MessageCreatedHandler makes.</summary>
    private static bool Decide(MessageCreated e) =>
        UnfurlDecision.ShouldUnfurlNew(e.EncryptionState, e.Type, e.EmbedsJson, e.Content);

    /// <summary>Mirrors the call MessageUpdatedHandler makes.</summary>
    private static bool Decide(MessageUpdated e) =>
        UnfurlDecision.ShouldUnfurlEdit(e.IsAuthorEdit, e.Flags, e.Content);

    private static MessageUpdated Updated(string content, Action<MessageUpdated>? customise = null)
    {
        var evt = new MessageUpdated
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AuthorId = "author-1",
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            IsAuthorEdit = true,
        };
        customise?.Invoke(evt);
        return evt;
    }

    // ── Create path ──────────────────────────────────────────────────────────

    [Test]
    public void PlainMessageWithALink_IsUnfurled()
    {
        Assert.That(Decide(Created("look at https://example.com")), Is.True);
    }

    [Test]
    public void MessageWithoutALink_IsNot()
    {
        Assert.That(Decide(Created("just talking")), Is.False);
    }

    [Test]
    public void EncryptedMessage_IsNever()
    {
        // Content is MLS ciphertext - the server cannot see a URL. A property of E2EE rather than a
        // gap to work around; the same wall that stops search indexing.
        Assert.That(Decide(Created("https://example.com",
            e => e.EncryptionState = MessageEncryptionState.Encrypted)), Is.False);
    }

    [Test]
    public void SystemMessage_IsNever()
    {
        Assert.That(Decide(Created("https://example.com",
            e => e.Type = MessageType.GuildMemberJoin)), Is.False);
    }

    [Test]
    public void MessageCarryingAuthorEmbeds_IsNot()
    {
        // Discord does not add link previews on top of a bot's own cards, and there is no sensible
        // way to order the two.
        var authored = GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]);

        Assert.That(Decide(Created("https://example.com", e => e.EmbedsJson = authored)), Is.False);
    }

    [Test]
    public void MessageCarryingOnlyGeneratedEmbeds_StillIs()
    {
        // Previously-generated previews are ours to replace - only author content stands us down.
        var generated = GeneratedEmbeds.Merge(null, [new EmbedPayload { Title = "old preview" }]);

        Assert.That(Decide(Created("https://example.com", e => e.EmbedsJson = generated)), Is.True);
    }

    [Test]
    public void LinkInsideACodeFence_IsNot()
    {
        Assert.That(Decide(Created("```\ncurl https://example.com\n```")), Is.False);
    }

    [Test]
    public void AngleBracketedLink_IsNot()
    {
        Assert.That(Decide(Created("quietly <https://example.com>")), Is.False);
    }

    // ── Edit path, and the loop it would otherwise cause ─────────────────────

    [Test]
    public void AuthorEditWithALink_IsReUnfurled()
    {
        Assert.That(Decide(Updated("now see https://example.com/other")), Is.True);
    }

    [Test]
    public void TheUnfurlersOwnUpdate_IsNot()
    {
        // THE loop guard. Attaching a preview publishes MessageUpdated like any other write, so
        // reacting to every update would unfurl, publish, unfurl, publish, forever. Only a human -
        // or a federated remote author - changing the text sets IsAuthorEdit.
        Assert.That(Decide(Updated("see https://example.com", e => e.IsAuthorEdit = false)), Is.False);
    }

    [Test]
    public void SuppressedMessage_IsNotReUnfurled()
    {
        // Dismissing a preview must not immediately rebuild it.
        Assert.That(Decide(Updated("see https://example.com",
            e => e.Flags = MessageFlags.SuppressEmbeds)), Is.False);
    }

    [Test]
    public void EditWithoutALink_IsNot()
    {
        Assert.That(Decide(Updated("no links any more")), Is.False);
    }
}
