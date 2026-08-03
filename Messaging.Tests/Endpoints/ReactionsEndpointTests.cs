using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers ReactionsEndpoint.AddReaction/RemoveReaction: custom emoji resolution via the cross-
/// service GetGuildEmojiRequest, unicode-emoji validation, and unauthenticated/validation-error
/// paths.
/// </summary>
[TestFixture]
public class ReactionsEndpointTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeMessagingHubContext _hub = null!;
    private ConversationPermissionService _permissions = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _hub = new FakeMessagingHubContext();
        _permissions = new ConversationPermissionService(_context, new FakeDistributedCache());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Seeds the message being reacted to.</summary>
    private async Task SeedMessageAsync(string messageId, string? channelId = null, string? conversationId = null)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hi"u8.ToArray(),
            ChannelId = channelId,
            ConversationId = conversationId,
            AuthorId = "user-author",
        });
        message.Id = messageId;

        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();
    }

    /// <summary>Makes the user a member of the conversation, which is what
    /// ConversationPermissionService checks.</summary>
    private async Task SeedConversationMemberAsync(string conversationId, string userId)
    {
        _context.Members.Add(ConversationMember.Create(new CreateConversationMemberParams
        {
            ConversationId = conversationId,
            UserId = userId,
            PublicKey = [1, 2, 3],
            CachedUserName = userId,
            CachedUserHash = 0,
        }));
        await _context.SaveChangesAsync();
    }

    private static FakeMessageBus BusAllowingChannel(Func<object, object>? extra = null) =>
        new(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = true, UserId = r.UserId, ChannelId = r.ChannelId, Permission = r.Permission,
            },
            _ => extra is not null ? extra(msg) : throw new InvalidOperationException("unexpected"),
        });

    // ══════════════════════════════════════════════════════════════════════════ AddReaction
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddReaction_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.Anonymous(), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_UnknownMessage_ReturnsNotFound()
    {
        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("no-such-message", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CallerNotInConversation_ReturnsForbid()
    {
        // The DTO used to decide the context, so a user removed from a group DM could keep
        // reacting into it - reactions are broadcast to every member and republished to Guild.
        await SeedMessageAsync("msg-1", conversationId: "conv-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("outsider"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
            Assert.That(_context.Reactions.Any(), Is.False, "nothing may be written for a forbidden caller");
        });
    }

    [Test]
    public async Task AddReaction_CallerLacksChannelPermission_ReturnsForbid()
    {
        await SeedMessageAsync("msg-1", channelId: "chan-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = false, UserId = r.UserId, ChannelId = r.ChannelId, Permission = r.Permission,
            },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateReactionDto { ChannelId = "chan-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("muted-user"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_ContextComesFromTheMessageNotTheDto()
    {
        // A mismatched DTO context used to decide the storage partition, writing reaction rows into
        // an unrelated context.
        await SeedMessageAsync("msg-1", conversationId: "conv-real");
        await SeedConversationMemberAsync("conv-real", "user-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-attacker-chose", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt!.ConversationId, Is.EqualTo("conv-real"));
        });
        Assert.That(_context.Reactions.Single().ContextId, Is.EqualTo("conv-real"));
    }

    [Test]
    public async Task AddReaction_CustomEmojiWithoutChannelId_ReturnsBadRequest()
    {
        await SeedMessageAsync("msg-1", conversationId: "conv-1");
        await SeedConversationMemberAsync("conv-1", "user-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CustomEmojiNotFoundInGuild_ReturnsNotFound()
    {
        await SeedMessageAsync("msg-1", channelId: "chan-1");

        var endpoint = new ReactionsEndpoint();
        var bus = BusAllowingChannel(msg => msg switch
        {
            GetGuildEmojiRequest => new GetGuildEmojiResponse { Found = false },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateReactionDto { ChannelId = "chan-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_CustomEmojiFound_ResolvesNameAndPersistsReaction()
    {
        await SeedMessageAsync("msg-1", channelId: "chan-1");

        var endpoint = new ReactionsEndpoint();
        var bus = BusAllowingChannel(msg => msg switch
        {
            GetGuildEmojiRequest => new GetGuildEmojiResponse { Found = true, Name = "custom_smile" },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateReactionDto { ChannelId = "chan-1", Reaction = "placeholder", EmojiId = "custom-1" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);
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
        await SeedMessageAsync("msg-1", conversationId: "conv-1");
        await SeedConversationMemberAsync("conv-1", "user-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "not-an-emoji" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AddReaction_ValidUnicodeEmoji_PersistsAndReturnsAccepted()
    {
        await SeedMessageAsync("msg-1", conversationId: "conv-1");
        await SeedConversationMemberAsync("conv-1", "user-1");

        var endpoint = new ReactionsEndpoint();
        var bus = new FakeMessageBus();
        var dto = new CreateReactionDto { ConversationId = "conv-1", Reaction = "👍" };

        var (result, evt) = await endpoint.AddReaction("msg-1", dto, _repo, TestPrincipal.ForUser("user-1"), _hub, bus, _permissions);
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

    // ══════════════════════════════════════════════════════════════════════════ RemoveReaction
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
        await SeedMessageAsync("msg-1", conversationId: "conv-1");
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
