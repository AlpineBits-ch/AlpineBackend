using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Handler.Messages;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Request;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// The central gate on <see cref="GetMessageRequest"/> and <see
/// cref="GetChannelMessagePagesRequest"/>.
/// </summary>
[TestFixture]
public class MessageReadAuthorizationTests
{
    private const string ChannelId = "chnl-open";
    private const string PrivateChannelId = "chnl-private";
    private const string ConversationId = "conv-1";
    private const string UserId = "user-reader";

    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private ConversationPermissionService _conversations = null!;

    /// <summary>Per-channel, per-permission answers.</summary>
    private readonly Dictionary<(string ChannelId, ExternalPermission Permission), bool> _denies = new();

    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _conversations = new ConversationPermissionService(_context, new FakeDistributedCache());
        _denies.Clear();
        _bus = new FakeMessageBus(Respond);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private object Respond(object message) => message switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
        {
            IsAllowed = Allowed(r.ChannelId, r.Permission),
            UserId = r.UserId,
            ChannelId = r.ChannelId,
            Permission = r.Permission,
        },
        FilterChannelsWithUserPermissionRequest r => new FilterChannelsWithUserPermissionResponse
        {
            UserId = r.UserId,
            AllowedChannelIds = r.ChannelIds.Where(id => Allowed(id, r.Permission)).ToList(),
        },
        _ => throw new NotSupportedException($"No canned response for {message.GetType().Name}"),
    };

    private bool Allowed(string channelId, ExternalPermission permission) =>
        !_denies.TryGetValue((channelId, permission), out var allowed) || allowed;

    private void Deny(string channelId, ExternalPermission permission) => _denies[(channelId, permission)] = false;

    private async Task<Message> SeedChannelMessageAsync(string channelId)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hello"u8.ToArray(),
            ChannelId = channelId,
            AuthorId = "user-author",
            ComponentsJson = "[{\"type\":1}]",
            EmbedsJson = "[{\"title\":\"t\"}]",
        });

        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();
        return message;
    }

    private async Task<Message> SeedConversationMessageAsync(params string[] memberUserIds)
    {
        foreach (var userId in memberUserIds)
        {
            _context.Members.Add(new ConversationMember
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                ConversationId = ConversationId,
                PublicKey = [],
                CachedUserName = userId,
                CachedUserHash = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var message = Message.Create(new CreateMessageParams
        {
            Content = "dm"u8.ToArray(),
            ConversationId = ConversationId,
            AuthorId = "user-author",
        });

        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();
        return message;
    }

    private Task<Contracts.Bus.Response.GetMessageResponse> InvokeAsync(
        string messageId, string userId = UserId, MessageReadScope scope = MessageReadScope.Full) =>
        GetMessageHandler.Handle(
            new GetMessageRequest { MessageId = messageId, RequestingUserId = userId, Scope = scope },
            _repo, _conversations, _bus, new RecordingLogger<GetMessageHandler>());

    // ══════════════════════════════════════════════════════════════════════════ GetMessageRequest
    // - the normal path ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChannelMessage_ViewAndHistoryAllowed_ReturnsTheBody()
    {
        var seeded = await SeedChannelMessageAsync(ChannelId);

        var response = await InvokeAsync(seeded.Id);

        Assert.That(response.Message, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response.Message!.Id, Is.EqualTo(seeded.Id));
            Assert.That(response.Message.Content, Is.EqualTo("hello"u8.ToArray()));
            Assert.That(response.Message.ComponentsJson, Is.Not.Null);
            Assert.That(response.Message.EmbedsJson, Is.Not.Null);
        });
    }

    [Test]
    public async Task ConversationMessage_Member_ReturnsTheBody()
    {
        var seeded = await SeedConversationMessageAsync(UserId);

        var response = await InvokeAsync(seeded.Id);

        Assert.That(response.Message, Is.Not.Null);
        Assert.That(response.Message!.Content, Is.EqualTo("dm"u8.ToArray()));
    }

    // ══════════════════════════════════════════════════════════════════════════ GetMessageRequest
    // - the refusals ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChannelMessage_ViewChannelDenied_IsRefusedAtTheHandler()
    {
        // No caller-side gate anywhere in this test: this is the fifth caller that forgot, and the
        // whole point of moving the check here is that it still gets nothing.
        var seeded = await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        var response = await InvokeAsync(seeded.Id);

        Assert.That(response.Message, Is.Null);
    }

    [Test]
    public async Task ChannelMessage_ReadMessageHistoryDenied_IsRefusedAtTheHandler()
    {
        // ViewChannel alone is not enough for a body: the two bits are independent, which is why
        // MessageHistoryAccess asks both questions rather than treating one as implying the other.
        var seeded = await SeedChannelMessageAsync(ChannelId);
        Deny(ChannelId, ExternalPermission.ReadMessageHistory);

        var response = await InvokeAsync(seeded.Id);

        Assert.That(response.Message, Is.Null);
    }

    [Test]
    public async Task RefusalIsIndistinguishableFromAMissingMessage()
    {
        var seeded = await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        var refused = await InvokeAsync(seeded.Id);
        var missing = await InvokeAsync("mesg-does-not-exist");

        Assert.Multiple(() =>
        {
            Assert.That(refused.Message, Is.Null);
            Assert.That(missing.Message, Is.Null);
        });
    }

    [Test]
    public async Task UnknownMessageId_AsksNoPermissionQuestion()
    {
        // Nothing to authorize against, and asking anyway would turn a miss into a bus round-trip
        // an unauthenticated id could provoke at will.
        var response = await InvokeAsync("mesg-does-not-exist");

        Assert.That(response.Message, Is.Null);
        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task ConversationMessage_NonMember_IsRefused()
    {
        var seeded = await SeedConversationMessageAsync("user-someone-else");

        var response = await InvokeAsync(seeded.Id);

        Assert.That(response.Message, Is.Null);
    }

    [Test]
    public async Task BlankPrincipal_IsRefusedWithoutAskingAnybody()
    {
        var seeded = await SeedChannelMessageAsync(ChannelId);

        var response = await InvokeAsync(seeded.Id, userId: "   ");

        Assert.That(response.Message, Is.Null);
        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task MessageWithNeitherChannelNorConversation_IsRefused()
    {
        // Not reachable through Message.Create, which insists on a context.
        var orphan = new Message
        {
            Id = Message.GenerateId(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AuthorId = "user-author",
            ContextId = "ctx-orphan",
            Content = "orphan"u8.ToArray(),
        };

        await _repo.CreateMessageAsync(orphan);
        await _context.SaveChangesAsync();

        var response = await InvokeAsync(orphan.Id);

        Assert.That(response.Message, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MessageReadScope.MetadataOnly - the one narrowed mode, and its limits
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MetadataOnly_WithHistoryDenied_ReturnsTimestampButNoBody()
    {
        // What the read-cursor ack asks for and all it is allowed to get.
        var seeded = await SeedChannelMessageAsync(ChannelId);
        Deny(ChannelId, ExternalPermission.ReadMessageHistory);

        var response = await InvokeAsync(seeded.Id, scope: MessageReadScope.MetadataOnly);

        Assert.That(response.Message, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response.Message!.CreatedAt, Is.EqualTo(seeded.CreatedAt));
            Assert.That(response.Message.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(response.Message.Content, Is.Empty);
            Assert.That(response.Message.ComponentsJson, Is.Null);
            Assert.That(response.Message.EmbedsJson, Is.Null);
        });
    }

    [Test]
    public async Task MetadataOnly_WithViewChannelDenied_IsStillRefused()
    {
        // The narrowing is of the projection and of the bit required, not of the check.
        var seeded = await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        var response = await InvokeAsync(seeded.Id, scope: MessageReadScope.MetadataOnly);

        Assert.That(response.Message, Is.Null);
    }

    [Test]
    public async Task MetadataOnly_OnAConversation_StillRequiresMembership()
    {
        var seeded = await SeedConversationMessageAsync("user-someone-else");

        var response = await InvokeAsync(seeded.Id, scope: MessageReadScope.MetadataOnly);

        Assert.That(response.Message, Is.Null);
    }

    [Test]
    public void DefaultScopeIsTheStrictOne()
    {
        // The failure mode of a caller that says nothing has to be a refusal, not a leak.
        var request = new GetMessageRequest { MessageId = "mesg-1", RequestingUserId = UserId };

        Assert.That(request.Scope, Is.EqualTo(MessageReadScope.Full));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetChannelMessagePagesRequest - the batched path
    // ══════════════════════════════════════════════════════════════════════════

    private Task<Contracts.Bus.Response.GetChannelMessagePagesResponse> InvokePagesAsync(
        params string[] channelIds) =>
        GetChannelMessagePagesHandler.Handle(
            new GetChannelMessagePagesRequest
            {
                RequestingUserId = UserId,
                Items = channelIds.Select(id => new ChannelMessagePageQuery { ChannelId = id }).ToList(),
            },
            _repo, _bus, new RecordingLogger<GetChannelMessagePagesHandler>());

    [Test]
    public async Task Pages_FiltersPerChannelRatherThanAllOrNothing()
    {
        await SeedChannelMessageAsync(ChannelId);
        await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        var response = await InvokePagesAsync(ChannelId, PrivateChannelId);

        Assert.That(response.Pages.Select(p => p.ChannelId), Is.EqualTo(new[] { ChannelId }));
        Assert.That(response.Pages.Single().Messages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pages_HistoryDenyIsHonouredPerChannel()
    {
        await SeedChannelMessageAsync(ChannelId);
        await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ReadMessageHistory);

        var response = await InvokePagesAsync(ChannelId, PrivateChannelId);

        Assert.That(response.Pages.Select(p => p.ChannelId), Is.EqualTo(new[] { ChannelId }));
    }

    [Test]
    public async Task Pages_CostsTwoRoundTripsWhateverTheBatchSize()
    {
        // The reason the batched contract exists.
        var channelIds = Enumerable.Range(0, 12).Select(i => $"chnl-{i}").ToArray();
        foreach (var channelId in channelIds) await SeedChannelMessageAsync(channelId);

        var response = await InvokePagesAsync(channelIds);

        Assert.That(response.Pages, Has.Count.EqualTo(channelIds.Length));
        Assert.That(_bus.Invoked, Has.Count.EqualTo(2));
        Assert.That(_bus.Invoked, Is.All.InstanceOf<FilterChannelsWithUserPermissionRequest>());
    }

    [Test]
    public async Task Pages_NothingVisible_StopsAfterTheFirstRoundTrip()
    {
        await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        var response = await InvokePagesAsync(PrivateChannelId);

        Assert.That(response.Pages, Is.Empty);
        Assert.That(_bus.Invoked, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pages_SecondRoundTripOnlyAsksAboutWhatTheFirstAllowed()
    {
        await SeedChannelMessageAsync(ChannelId);
        await SeedChannelMessageAsync(PrivateChannelId);
        Deny(PrivateChannelId, ExternalPermission.ViewChannel);

        await InvokePagesAsync(ChannelId, PrivateChannelId);

        var history = _bus.Invoked
            .OfType<FilterChannelsWithUserPermissionRequest>()
            .Single(r => r.Permission == ExternalPermission.ReadMessageHistory);

        Assert.That(history.ChannelIds, Is.EqualTo(new[] { ChannelId }));
    }

    [Test]
    public async Task Pages_BlankPrincipal_ReturnsNothingAndAsksNobody()
    {
        await SeedChannelMessageAsync(ChannelId);

        var response = await GetChannelMessagePagesHandler.Handle(
            new GetChannelMessagePagesRequest
            {
                RequestingUserId = "  ",
                Items = [new ChannelMessagePageQuery { ChannelId = ChannelId }],
            },
            _repo, _bus, new RecordingLogger<GetChannelMessagePagesHandler>());

        Assert.That(response.Pages, Is.Empty);
        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task Pages_EmptyBatch_AsksNobody()
    {
        var response = await GetChannelMessagePagesHandler.Handle(
            new GetChannelMessagePagesRequest { RequestingUserId = UserId, Items = [] },
            _repo, _bus, new RecordingLogger<GetChannelMessagePagesHandler>());

        Assert.That(response.Pages, Is.Empty);
        Assert.That(_bus.Invoked, Is.Empty);
    }
}
