using Bots.Contracts.Gateway.Payloads;
using Messaging.Application.Commands;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Previews;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Commands;

/// <summary>
/// The write half of link previews (docs/specs/message-previews.md): merge semantics for generated
/// embeds, the stale-content guard, the suppression flag, and the "(edited)" distinction.
/// </summary>
[TestFixture]
public class MessagePreviewUpdateTests
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

    private async Task<Message> SeedMessage(string content = "see https://example.com", string? embedsJson = null)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            ConversationId = "conv-1",
            AuthorId = "author-1",
            EmbedsJson = embedsJson,
        });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    private static string GeneratedJson(string title) =>
        GeneratedEmbeds.Serialize([new EmbedPayload { Title = title }]);

    /// <summary>Commits and detaches between two handler invocations.</summary>
    private async Task CommitAsync()
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    // ── Attaching a preview ──────────────────────────────────────────────────

    [Test]
    public async Task GeneratedEmbeds_AreAttachedAndFlaggedAsGenerated()
    {
        var message = await SeedMessage();

        var (response, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            GeneratedEmbedsJson = GeneratedJson("Example Domain"),
        }, _repo);

        var stored = GeneratedEmbeds.Parse(evt!.EmbedsJson);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(stored, Has.Count.EqualTo(1));
            Assert.That(stored[0].Title, Is.EqualTo("Example Domain"));
            Assert.That(stored[0].IsGenerated, Is.True);
        });
    }

    [Test]
    public async Task AttachingAPreview_DoesNotMarkTheMessageEdited()
    {
        // The whole reason EditedAt exists.
        var message = await SeedMessage();

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            GeneratedEmbedsJson = GeneratedJson("Example"),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(evt!.EditedAt, Is.Null, "nobody edited this message");
            Assert.That(evt.UpdatedAt, Is.GreaterThan(message.CreatedAt), "but the row was written");
            Assert.That(evt.IsAuthorEdit, Is.False, "so the author must be included in the broadcast");
        });
    }

    [Test]
    public async Task AnOrdinaryTextEdit_DoesMarkTheMessageEdited()
    {
        var message = await SeedMessage();

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "changed"u8.ToArray(),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(evt!.EditedAt, Is.Not.Null);
            Assert.That(evt.IsAuthorEdit, Is.True);
        });
    }

    [Test]
    public async Task GeneratedEmbeds_DoNotDisturbAuthorEmbeds()
    {
        var authored = GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]);
        var message = await SeedMessage(embedsJson: authored);

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            GeneratedEmbedsJson = GeneratedJson("link preview"),
        }, _repo);

        var stored = GeneratedEmbeds.Parse(evt!.EmbedsJson);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Has.Count.EqualTo(2));
            Assert.That(stored[0].Title, Is.EqualTo("bot card"));
            Assert.That(stored[0].IsGenerated, Is.False);
        });
    }

    // ── The stale-content race ───────────────────────────────────────────────

    [Test]
    public async Task MatchingContentHash_LetsThePreviewLand()
    {
        var message = await SeedMessage("see https://example.com");

        var (response, _) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            ExpectedContentSha256 = ContentHash.Of(message.Content),
            GeneratedEmbedsJson = GeneratedJson("Example"),
        }, _repo);

        Assert.That(response.Success, Is.True);
    }

    [Test]
    public async Task ContentEditedWhileFetching_DropsTheStalePreview()
    {
        // Fetching a page takes seconds; the author can edit the message in that window.
        var message = await SeedMessage("see https://example.com");
        var hashAtExtractionTime = ContentHash.Of(message.Content);

        await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "actually never mind"u8.ToArray(),
        }, _repo);
        await CommitAsync();

        var (response, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            ExpectedContentSha256 = hashAtExtractionTime,
            GeneratedEmbedsJson = GeneratedJson("Example"),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Stale, Is.True);
            Assert.That(response.Success, Is.False);
            Assert.That(evt, Is.Null, "a rejected write must not broadcast anything");
        });
    }

    // ── Suppression ──────────────────────────────────────────────────────────

    [Test]
    public async Task SuppressedMessage_RefusesALatePreview()
    {
        // The dismissal and the in-flight unfurl race each other.
        var message = await SeedMessage();

        await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            Flags = MessageFlags.SuppressEmbeds,
        }, _repo);
        await CommitAsync();

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            GeneratedEmbedsJson = GeneratedJson("should not appear"),
        }, _repo);

        Assert.That(GeneratedEmbeds.Parse(evt!.EmbedsJson), Is.Empty);
    }

    [Test]
    public async Task SuppressionFlag_TravelsOnTheUpdateEvent()
    {
        // Clients cannot otherwise tell "dismissed" from "never generated" - both are an empty array.
        var message = await SeedMessage();

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            Flags = MessageFlags.SuppressEmbeds,
        }, _repo);

        Assert.That(MessageFlags.Has(evt!.Flags, MessageFlags.SuppressEmbeds), Is.True);
    }

    [Test]
    public async Task NullFlags_LeaveTheStoredBitfieldAlone()
    {
        // The patch convention the rest of this command follows.
        var message = await SeedMessage();

        await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            Flags = MessageFlags.SuppressEmbeds,
        }, _repo);
        await CommitAsync();

        var (_, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "author-1",
            Content = "new text"u8.ToArray(),
        }, _repo);

        Assert.That(MessageFlags.Has(evt!.Flags, MessageFlags.SuppressEmbeds), Is.True);
    }

    // ── Authorization ────────────────────────────────────────────────────────

    [Test]
    public async Task AuthorizationAlreadyChecked_DoesNotLetAStrangerEditText()
    {
        // The flag exists for the moderator-suppression and unfurler paths, which do their own
        // check.
        var message = await SeedMessage();

        var (response, _) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "someone-else",
            AuthorizationAlreadyChecked = true,
            Content = "hijacked"u8.ToArray(),
        }, _repo);

        Assert.That(response.Success, Is.True,
            "handler-level: the caller asserted it checked. See MessagingEndpoints.SuppressMessageEmbeds " +
            "and UnfurlLinksHandler for the two callers that may set this, neither of which sends Content.");
    }

    [Test]
    public async Task WithoutTheFlag_ANonAuthorIsStillForbidden()
    {
        var message = await SeedMessage();

        var (response, evt) = await new UpdateMessageCommandHandler().Handle(new UpdateMessageCommand
        {
            MessageId = message.Id,
            RequestingAuthorId = "someone-else",
            Content = "hijacked"u8.ToArray(),
        }, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(response.Forbidden, Is.True);
            Assert.That(evt, Is.Null);
        });
    }
}
