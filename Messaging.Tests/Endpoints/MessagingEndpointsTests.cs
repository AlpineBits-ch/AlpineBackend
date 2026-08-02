using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using MessageDeleted = Messaging.Domain.Events.Message.MessageDeleted;
using PinMessageCommand = Messaging.Contracts.Bus.Commands.PinMessageCommand;
using UnpinMessageCommand = Messaging.Contracts.Bus.Commands.UnpinMessageCommand;
using UpdateMessageCommand = Messaging.Contracts.Bus.Commands.UpdateMessageCommand;
using MessageDto = global::Messaging.Application.Dtos.Response.MessageDto;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers MessagingEndpoints: CreateMessage's channel-permission/automod/conversation-membership
/// gating, DeleteMessage's author-only check, UpdateMessageAsync/PinMessage/UnpinMessage/
/// GetPinnedMessages' auth and not-found/forbidden routing.
/// </summary>
[TestFixture]
public class MessagingEndpointsTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeDistributedCache _cache = null!;
    private ConversationPermissionService _permissionService = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _cache = new FakeDistributedCache();
        _permissionService = new ConversationPermissionService(_context, _cache);
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

    /// <summary>CreateMessage consults the context's MLS state to decide whether plaintext is
    /// acceptable. With no generation rows seeded, every context in this fixture reads as
    /// unencrypted, so these tests keep exercising the plaintext path exactly as before.</summary>
    private MlsGroupService MakeMlsService(FakeMessageBus bus) =>
        new(_context, new FakeMessagingHubContext(), bus, new MlsJoinRequestService(_context),
            Helpers.TestMlsServices.Coverage(bus));

    private static Message FakeHandlerReturnFor(CreateMessageCommand cmd) => Message.Create(new CreateMessageParams
    {
        Content = cmd.Content,
        ChannelId = cmd.ChannelId,
        ConversationId = cmd.ConversationId,
        AuthorId = cmd.AuthorId,
    });

    // ══════════════════════════════════════════════════════════════════════════ CreateMessage
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.Anonymous(), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_NoChannelOrConversationId_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_LacksSendMessagesPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateMessageDto { Content = "hi", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_AutoModBlocksWord_ReturnsForbiddenJson_AndPublishesAutoModTriggered()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = ["badword"] },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateMessageDto { Content = "this has a badword in it", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(403));
            Assert.That(evt, Is.Null);
            Assert.That(bus.Published.Any(p => p is AutoModTriggeredEvent), Is.True);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_LacksMentionEveryone_StripsMentionFlagsButStillSends()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            // Allowed to speak, not allowed to ping the room.
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = r.Permission != ExternalPermission.MentionEveryone,
                Permission = r.Permission,
            },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "@everyone lunch?", ChannelId = "chan-1", MentionsEveryone = true, MentionsHere = true };

        var (result, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>(), "the message still sends - only the ping is dropped");
            Assert.That(command.MentionsEveryone, Is.False);
            Assert.That(command.MentionsHere, Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Encryption-state
    // enforcement

    private FakeMessageBus ChannelSendBus() => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
        GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
        CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    private async Task EncryptChannel(string channelId, int generation = 1, long epoch = 1)
    {
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = channelId,
            ChannelId = channelId,
            Generation = generation,
            MlsGroupId = [1, 2, 3],
            Epoch = epoch,
            ActivatedByUserId = "user-1",
            ActivatedAt = DateTimeOffset.UtcNow,
        }));
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task CreateMessage_PlaintextIntoAnEncryptedChannel_IsRefused()
    {
        await EncryptChannel("chan-1");
        var endpoint = new MessagingEndpoints();
        var bus = ChannelSendBus();
        var dto = new CreateMessageDto { Content = "oops", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(),
            TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Conflict<MlsSendConflictDto>>());
            Assert.That(evt, Is.Null);
            Assert.That(bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty, "nothing may be stored");
        });
    }

    [Test]
    public async Task CreateMessage_EncryptedIntoAPlainChannel_IsRefused()
    {
        var endpoint = new MessagingEndpoints();
        var bus = ChannelSendBus();
        var dto = new CreateMessageDto
        {
            Content = "ciphertext",
            ChannelId = "chan-1",
            EncryptionState = global::Messaging.Domain.Enums.MessageEncryptionState.Encrypted,
        };

        var (result, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(),
            TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        // Nobody joining later could read it, and the sender clearly has a stale view of the room.
        Assert.That(result, Is.InstanceOf<Conflict<MlsSendConflictDto>>());
    }

    [Test]
    public async Task CreateMessage_EncryptedWithoutAGeneration_IsStampedWithTheLiveOne()
    {
        await EncryptChannel("chan-1", generation: 3);
        var endpoint = new MessagingEndpoints();
        var bus = ChannelSendBus();
        var dto = new CreateMessageDto
        {
            Content = "ciphertext",
            ChannelId = "chan-1",
            EncryptionState = global::Messaging.Domain.Enums.MessageEncryptionState.Encrypted,
        };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(),
            TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        // A client that predates generations sends none, and the only group it could have encrypted
        // against is the live one - so stamp it rather than refusing a correct message.
        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(command.MlsGeneration, Is.EqualTo(3));
    }

    [Test]
    public async Task CreateMessage_EncryptedUnderAReplacedGeneration_IsRefused()
    {
        await EncryptChannel("chan-1", generation: 2);
        var endpoint = new MessagingEndpoints();
        var bus = ChannelSendBus();
        var dto = new CreateMessageDto
        {
            Content = "ciphertext",
            ChannelId = "chan-1",
            EncryptionState = global::Messaging.Domain.Enums.MessageEncryptionState.Encrypted,
            MlsGeneration = 1,
        };

        var (result, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(),
            TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        // Sealed to a group that has since been replaced - nobody in the channel can read it.
        var conflict = (Conflict<MlsSendConflictDto>)result;
        Assert.That(conflict.Value!.ActiveGeneration, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateMessage_PlaintextIntoAPlainChannel_IsUnaffected()
    {
        var endpoint = new MessagingEndpoints();
        var bus = ChannelSendBus();
        var dto = new CreateMessageDto { Content = "ordinary", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(),
            TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.InstanceOf<Conflict<MlsSendConflictDto>>());
            Assert.That(evt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_HasMentionEveryone_KeepsMentionFlags()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "@everyone deploy is out", ChannelId = "chan-1", MentionsEveryone = true, MentionsHere = true };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.MentionsEveryone, Is.True);
            Assert.That(command.MentionsHere, Is.True);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_NoMentionFlagsRequested_SkipsThePermissionCheckEntirely()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "ordinary message", ChannelId = "chan-1" };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.That(bus.Invoked.OfType<HasUserPermissionToChannelRequest>()
                .Any(r => r.Permission == ExternalPermission.MentionEveryone), Is.False,
            "the extra round trip must only happen when the client actually asked for a ping");
    }

    [Test]
    public async Task CreateMessage_ConversationScope_KeepsMentionFlagsWithoutPermissionCheck()
    {
        const string conversationId = "conv-mention";
        _context.Conversations.Add(new Conversation
        {
            Id = conversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members = [MakeMember("cm-1", "user-1", conversationId)],
        });
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "@everyone", ConversationId = conversationId, MentionsEveryone = true };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.MentionsEveryone, Is.True, "a DM has no MentionEveryone permission concept");
            Assert.That(bus.Invoked.OfType<HasUserPermissionToChannelRequest>(), Is.Empty);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_SlowModeSecondSend_Returns429WithRetryAfter()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = true, Permission = r.Permission, SlowModeSeconds = 30, CanBypassSlowMode = false,
            },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hi", ChannelId = "chan-slow" };

        var (first, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));
        var (second, secondEvt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(second, Is.InstanceOf<IStatusCodeHttpResult>());
            Assert.That(((IStatusCodeHttpResult)second).StatusCode, Is.EqualTo(429));
            Assert.That(secondEvt, Is.Null, "a throttled send must not emit MessageCreated");
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_SlowModeButUserCanBypass_Allows()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = true, Permission = r.Permission, SlowModeSeconds = 30, CanBypassSlowMode = true,
            },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hi", ChannelId = "chan-slow" };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("mod-1"), _context, bus, _cache, MakeMlsService(bus));
        var (second, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("mod-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.That(second, Is.InstanceOf<Created<MessageDto>>());
    }

    [Test]
    public async Task CreateMessage_ChannelScope_SlowModeDoesNotApplyToBots()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = true, Permission = r.Permission, SlowModeSeconds = 30, CanBypassSlowMode = false,
            },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            // No auto-mod branch and no slowmode rejection: a Bot author skips both gates.
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hi", ChannelId = "chan-slow" };

        await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("bot-1", userType: "Bot"), _context, bus, _cache, MakeMlsService(bus));
        var (second, _) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("bot-1", userType: "Bot"), _context, bus, _cache, MakeMlsService(bus));

        Assert.That(second, Is.InstanceOf<Created<MessageDto>>());
    }

    [Test]
    public async Task CreateMessage_ChannelScope_AutoModBlock_DoesNotConsumeSlowModeWindow()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = true, Permission = r.Permission, SlowModeSeconds = 30, CanBypassSlowMode = false,
            },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = ["badword"] },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        // Blocked by auto-mod, so it must never have reached the slowmode gate...
        var (blocked, _) = await endpoint.CreateMessage(
            new CreateMessageDto { Content = "badword", ChannelId = "chan-slow" },
            ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        // ...so the author's very next clean message is still their first real send.
        var (clean, _) = await endpoint.CreateMessage(
            new CreateMessageDto { Content = "sorry", ChannelId = "chan-slow" },
            ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)blocked).StatusCode, Is.EqualTo(403));
            Assert.That(clean, Is.InstanceOf<Created<MessageDto>>());
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_BotAuthor_BypassesAutoMod_EvenIfWordWouldBeBlocked()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            // No GetGuildAutoModConfigRequest branch: a Bot author must never reach AutoModeration.CheckAsync.
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "badword", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("bot-1", userType: "Bot"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_AllowedAndClean_ReturnsCreated()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hello world", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(evt.AuthorId, Is.EqualTo("user-1"));
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_ConversationDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-missing" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_UserNotAMember_ReturnsForbid()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "other-user", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_UserIsMember_ReturnsCreated()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "user-1", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache, MakeMlsService(bus));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.ConversationId, Is.EqualTo("conv-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ DeleteMessage
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage("msg-1", _repo, TestPrincipal.Anonymous(), new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage("nope", _repo, TestPrincipal.ForUser("user-1"), new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_RequesterIsNotAuthor_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("someone-else"), new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_AuthorDeletesOwnMessage_ReturnsAccepted()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        // GetMessageAsync (inside DeleteMessage) reads via AsNoTracking, producing a second
        // instance sharing this key - without clearing the tracker first, DeleteMessageAsync's
        // later Remove() throws EF's identity-conflict exception (test-setup artifact, see the
        // matching comment in PinMessageCommandTests.SeedMessage).
        _context.ChangeTracker.Clear();

        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("author-1"), new FakeMessageBus());
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.MessageId, Is.EqualTo(message.Id));
        });
        Assert.That(_context.Messages.Any(m => m.Id == message.Id), Is.False);
    }

    [Test]
    public async Task DeleteMessage_NonAuthorWithDeleteAnyMessage_InChannel_Deletes()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("moderator-1"), bus);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(bus.Invoked.OfType<HasUserPermissionToChannelRequest>().Single().Permission,
                Is.EqualTo(ExternalPermission.DeleteAnyMessage));
        });
        Assert.That(_context.Messages.Any(m => m.Id == message.Id), Is.False);
    }

    [Test]
    public async Task DeleteMessage_NonAuthorWithoutDeleteAnyMessage_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var endpoint = new MessagingEndpoints();

        var (result, _) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("nobody"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(_context.Messages.Any(m => m.Id == message.Id), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════ BulkDeleteMessages
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Seeds n channel messages and returns their ids, tracker cleared - same
    /// identity-conflict reason as DeleteMessage_AuthorDeletesOwnMessage above.</summary>
    private async Task<List<string>> SeedChannelMessages(int count, string channelId)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var message = Message.Create(new CreateMessageParams
            {
                Content = "hi"u8.ToArray(), ChannelId = channelId, AuthorId = $"author-{i}",
            });
            await _context.Messages.AddAsync(message);
            ids.Add(message.Id);
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return ids;
    }

    private static FakeMessageBus AllowingBus(bool allowed = true) => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = allowed, Permission = r.Permission },
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    [Test]
    public async Task BulkDelete_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = ["a"] };

        var result = await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.Anonymous(), AllowingBus(), NullLogger<MessagingEndpoints>.Instance);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task BulkDelete_LacksDeleteAnyMessage_ReturnsForbid()
    {
        var ids = await SeedChannelMessages(2, "chan-1");
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = ids };

        var result = await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("user-1"), AllowingBus(allowed: false), NullLogger<MessagingEndpoints>.Instance);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(_context.Messages.Count(), Is.EqualTo(2), "nothing may be removed on a denied call");
    }

    [Test]
    public async Task BulkDelete_OverTheCap_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto
        {
            ChannelId = "chan-1",
            MessageIds = Enumerable.Range(0, 101).Select(i => $"msg-{i}").ToList(),
        };

        var result = await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("mod-1"), AllowingBus(), NullLogger<MessagingEndpoints>.Instance);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task BulkDelete_EmptyIds_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = [] };

        var result = await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("mod-1"), AllowingBus(), NullLogger<MessagingEndpoints>.Instance);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task BulkDelete_Valid_RemovesAllAndPublishesPerMessageAndAggregateEvents()
    {
        var ids = await SeedChannelMessages(3, "chan-1");
        var bus = AllowingBus();
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = ids };

        var result = await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("mod-1"), bus, NullLogger<MessagingEndpoints>.Instance);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(200));
            Assert.That(_context.Messages.Count(), Is.Zero);
            Assert.That(bus.Published.OfType<MessageDeleted>().Count(), Is.EqualTo(3),
                "the per-message pipeline (search index, bot MESSAGE_DELETE, reply counts) must still run for each");
            Assert.That(bus.Published.OfType<MessagesBulkDeletedForChannel>().Count(), Is.EqualTo(1),
                "plus exactly one aggregate event for the client's single-update path");
        });
    }

    [Test]
    public async Task BulkDelete_IdsFromAnotherChannel_AreSkippedNotDeleted()
    {
        var mine = await SeedChannelMessages(2, "chan-1");
        var theirs = await SeedChannelMessages(2, "chan-2");
        var bus = AllowingBus();
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = [.. mine, .. theirs] };

        await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("mod-1"), bus, NullLogger<MessagingEndpoints>.Instance);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Messages.Count(), Is.EqualTo(2), "the other channel's messages survive");
            Assert.That(_context.Messages.All(m => m.ChannelId == "chan-2"), Is.True);
            Assert.That(bus.Published.OfType<MessageDeleted>().Count(), Is.EqualTo(2),
                "the permission check covered chan-1 only, so chan-2 ids must not be acted on");
        });
    }

    [Test]
    public async Task BulkDelete_AllIdsUnknown_ReturnsZeroAndPublishesNothing()
    {
        var bus = AllowingBus();
        var endpoint = new MessagingEndpoints();
        var dto = new BulkDeleteMessagesDto { ChannelId = "chan-1", MessageIds = ["nope-1", "nope-2"] };

        await endpoint.BulkDeleteMessages(dto, _repo, TestPrincipal.ForUser("mod-1"), bus, NullLogger<MessagingEndpoints>.Instance);

        Assert.That(bus.Published, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ UpdateMessageAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.Anonymous(), bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateMessage_HandlerReportsNotFound_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { NotFound = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateMessage_HandlerReportsForbidden_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { Forbidden = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateMessage_Success_ReturnsAccepted()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { Success = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(202));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ PinMessage /
    // UnpinMessage ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PinMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage("msg-1", _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task PinMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage("nope", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task PinMessage_ChannelMessage_LacksPinPermission_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PinMessage_ConversationMessage_UserLacksMembership_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PinMessage_ConversationMessage_MemberWithPermission_ReturnsOkResult()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            PinMessageCommand => new PinMessageResponse { Success = true, PinnedById = "user-1" },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<PinMessageResponse>>());
    }

    [Test]
    public async Task PinMessage_HandlerReportsNotFound_ReturnsNotFound()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            PinMessageCommand => new PinMessageResponse { NotFound = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UnpinMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage("msg-1", _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UnpinMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage("nope", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UnpinMessage_ChannelMessage_HasPermission_ReturnsOk()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            UnpinMessageCommand => new PinMessageResponse { Success = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        var result = await endpoint.UnpinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<PinMessageResponse>>());
    }

    [Test]
    public async Task UnpinMessage_MessageHasNeitherChannelNorConversation_ReturnsNotFound()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-x", AuthorId = "author-1" });
        message.ConversationId = null;
        message.ChannelId = null;
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════ GetPinnedMessages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPinnedMessages_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages("chan-1", null, _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_NoChannelOrConversationId_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, null, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<BadRequest>());
    }

    [Test]
    public async Task GetPinnedMessages_ChannelScope_LacksViewPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.GetPinnedMessages("chan-1", null, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_ConversationScope_UserLacksPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, "conv-1", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_ConversationScope_UserIsMember_ReturnsPinnedMessages()
    {
        var pinned = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        pinned.IsPinned = true;
        pinned.PinnedAt = DateTime.UtcNow;
        pinned.PinnedById = "user-1";
        await _context.Messages.AddAsync(pinned);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, "conv-1", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<IEnumerable<MessageDto>>>());
        var ok = (Ok<IEnumerable<MessageDto>>)result;
        Assert.That(ok.Value!.Select(m => m.Id), Contains.Item(pinned.Id));
    }
}
