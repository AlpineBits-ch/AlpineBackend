using System.Text;
using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Messaging.Application.Handler.Account;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Messaging's participant in the export fan-out (T1-7).
///
/// <para><b>The negative test is the point.</b> Every conversation in this store is shared, so an
/// export scoped by conversation rather than by author would hand a subject a full transcript of what
/// other people wrote to them - their words, their timing, their devices - without ever asking those
/// people. The author filter is the whole of the difference between a portability feature and a
/// disclosure of somebody else's messages.</para>
/// </summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private const string Subject = "user_subject";
    private const string Other = "user_other";
    private const string ConversationId = "conv_shared";

    private TestMessagingContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestMessagingContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ConversationMember MakeMember(string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = userId,
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedConversationAsync()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            Name = "Shared DM",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Members.Add(MakeMember("cmem_subject", Subject, ConversationId));
        _context.Members.Add(MakeMember("cmem_other", Other, ConversationId));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<Message> SeedMessageAsync(string authorId, string body, int minutesAgo)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = Encoding.UTF8.GetBytes(body),
            ConversationId = ConversationId,
            AuthorId = authorId,
        });

        var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        message.CreatedAt = at;
        message.UpdatedAt = at;

        _context.Messages.Add(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return message;
    }

    private Task<Identity.Contracts.Bus.Response.ExportUserDataResponse> ExportAsync(string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = userId },
            _context,
            new EfCoreMessageRepository(_context),
            NullLogger<ExportUserDataCommandHandler>.Instance);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheSubjectsOwnMessagesAndMembership()
    {
        await SeedConversationAsync();
        await SeedMessageAsync(Subject, "mine one", 30);
        await SeedMessageAsync(Subject, "mine two", 20);

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(response.Service, Is.EqualTo("messaging"));
            Assert.That(response.RowCounts["messages"], Is.EqualTo(2));
            Assert.That(response.RowCounts["memberships"], Is.EqualTo(1));
            Assert.That(response.RowCounts["conversations"], Is.EqualTo(1));
        });

        using var document = JsonDocument.Parse(response.FragmentJson);
        var conversation = document.RootElement.GetProperty("conversations").EnumerateArray().Single();
        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();

        var bodies = messages
            .Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.GetProperty("contentBase64").GetString()!)))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(conversation.GetProperty("truncated").GetBoolean(), Is.False);
            Assert.That(bodies, Does.Contain("mine one"));
            Assert.That(bodies, Does.Contain("mine two"));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AccountInNoConversations_ReturnsAnEmptyFragment()
    {
        var response = await ExportAsync("user_nobody");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["conversations"], Is.EqualTo(0));
            Assert.That(response.RowCounts["messages"], Is.EqualTo(0));
            Assert.That(response.Error, Is.Null);
        });
    }

    [Test]
    public async Task Handle_ConversationWithNoMessagesFromTheSubject_IsStillListed()
    {
        await SeedConversationAsync();
        await SeedMessageAsync(Other, "only theirs", 10);

        var response = await ExportAsync(Subject);

        using var document = JsonDocument.Parse(response.FragmentJson);
        var conversation = document.RootElement.GetProperty("conversations").EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            // The subject is entitled to know they are in the conversation, and to none of its
            // contents that are not theirs.
            Assert.That(conversation.GetProperty("conversationId").GetString(), Is.EqualTo(ConversationId));
            Assert.That(conversation.GetProperty("messages").GetArrayLength(), Is.Zero);
            Assert.That(response.RowCounts["messages"], Is.EqualTo(0));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotExportAnotherParticipantsMessages()
    {
        await SeedConversationAsync();
        await SeedMessageAsync(Subject, "mine", 30);
        await SeedMessageAsync(Other, "THEIR PRIVATE WORDS", 25);

        var response = await ExportAsync(Subject);

        var theirs = Convert.ToBase64String(Encoding.UTF8.GetBytes("THEIR PRIVATE WORDS"));

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["messages"], Is.EqualTo(1));
            Assert.That(response.FragmentJson, Does.Not.Contain(theirs));
            Assert.That(response.FragmentJson, Does.Not.Contain("THEIR PRIVATE WORDS"));
        });
    }

    [Test]
    public async Task Handle_DoesNotExportTheOtherMembersOfAConversation()
    {
        await SeedConversationAsync();
        await SeedMessageAsync(Subject, "mine", 5);

        var response = await ExportAsync(Subject);

        // A group's membership is a fact about the whole group, not about one member of it.
        Assert.That(response.FragmentJson, Does.Not.Contain(Other));
        Assert.That(response.FragmentJson, Does.Not.Contain("cmem_other"));
    }
}
