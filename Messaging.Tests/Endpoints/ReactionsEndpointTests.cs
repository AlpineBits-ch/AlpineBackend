using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Endpoints;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers ReactionsEndpoint.AddReaction/RemoveReaction: custom emoji resolution via the cross-
/// service GetGuildEmojiRequest, unicode-emoji validation, and unauthenticated/validation-error
/// paths. The endpoint itself doesn't fan out over the hub directly (it returns a ReactionCreated/
/// ReactionRemoved event for Wolverine to dispatch to ReactionCreatedHandler/ReactionDeletedHandler
/// - covered separately).
/// </summary>
[TestFixture]
public class ReactionsEndpointTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════
    // AddReaction
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddReaction_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.Anonymous(), _hub, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CustomEmojiWithoutChannelId_ReturnsBadRequest()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CustomEmojiNotFoundInGuild_ReturnsNotFound()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildEmojiRequest => new GetGuildEmojiResponse { Found = false },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateReactionDto { ChannelId = "chan-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CustomEmojiFound_ResolvesNameAndPersistsReaction()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildEmojiRequest => new GetGuildEmojiResponse { Found = true, Name = "custom_smile" },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateReactionDto { ChannelId = "chan-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.Emoji, Is.EqualTo("custom_smile"), "The resolved custom emoji name must be used, not the placeholder");
            Assert.That(evt.EmojiId, Is.EqualTo("custom-1"));
        });
        Assert.That(_context.Reactions.Any(r => r.Emoji == "custom_smile" && r.EmojiId == "custom-1"), Is.True);
    }

    [Test]
    public async Task AddReaction_InvalidUnicodeEmoji_ReturnsBadRequest()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "not-an-emoji" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_ValidUnicodeEmoji_PersistsAndReturnsAccepted()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.UserId, Is.EqualTo("user-1"));
            Assert.That(evt.ConversationId, Is.EqualTo("conv-1"));
        });
        Assert.That(_context.Reactions.Any(r => r.MessageId == "msg-1" && r.UserId == "user-1"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RemoveReaction
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveReaction_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new RemoveReactionDto { ContextId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.RemoveReaction("msg-1", dto, _repo, TestPrincipal.Anonymous(), _hub, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task RemoveReaction_RemovesOnlyMatchingReaction_AndReturnsEvent()
    {
        await _repo.AddReactionAsync(Reaction.Create(new CreateReactionParams { MessageId = "msg-1", UserId = "user-1", Emoji = "👍", ConversationId = "conv-1" }));
        await _repo.AddReactionAsync(Reaction.Create(new CreateReactionParams { MessageId = "msg-1", UserId = "user-2", Emoji = "👍", ConversationId = "conv-1" }));
        await _context.SaveChangesAsync();

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new RemoveReactionDto { ContextId = "conv-1", ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.RemoveReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.UserId, Is.EqualTo("user-1"));
        });

        var remaining = _context.Reactions.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(1));
            Assert.That(remaining[0].UserId, Is.EqualTo("user-2"));
        });
    }
}
