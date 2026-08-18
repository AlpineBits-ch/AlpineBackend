using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using CreateMessageCommand = Messaging.Contracts.Bus.Commands.CreateMessageCommand;
using MessageDto = global::Messaging.Application.Dtos.Response.MessageDto;
using UpdateMessageCommand = Messaging.Contracts.Bus.Commands.UpdateMessageCommand;
using UpdateMessageResponse = Messaging.Contracts.Bus.Response.UpdateMessageResponse;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// The ceiling at the send and edit endpoints: what is accepted, what is refused, and what the
/// refusal tells the client.
/// </summary>
[TestFixture]
public class MessageLengthEnforcementTests
{
    private const string ChannelId = "chan-1";
    private const string GuildId = "guild-1";
    private const string UserId = "user-1";
    private const int PlanLimit = 4000;

    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ create

    [Test]
    public async Task A_post_of_exactly_the_limit_is_accepted()
    {
        var bus = SendBus();

        var result = await SendAsync(bus, new string('a', PlanLimit));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(bus.Invoked.OfType<CreateMessageCommand>(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task A_post_one_character_over_the_limit_is_refused_and_nothing_is_stored()
    {
        var bus = SendBus();

        var result = await SendAsync(bus, new string('a', PlanLimit + 1));

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode,
                Is.EqualTo(MessageLengthPolicy.TooLongStatusCode));
            Assert.That(bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty,
                "nothing may be stored, so nothing is announced");
        });
    }

    /// <summary>
    /// The refusal has to be usable as "this server allows 4,000 characters and this is 4,180", so
    /// both numbers are fields rather than prose.
    /// </summary>
    [Test]
    public async Task The_refusal_names_the_limit_and_the_length()
    {
        var bus = SendBus();

        var result = await SendAsync(bus, new string('a', 4180));
        var body = ((IValueHttpResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(Read(body, "error"), Is.EqualTo(MessageLengthPolicy.TooLongError));
            Assert.That(Read(body, "maxLength"), Is.EqualTo(PlanLimit));
            Assert.That(Read(body, "length"), Is.EqualTo(4180));
        });
    }

    /// <summary>
    /// The regression a byte count would cause: a Cyrillic post inside the limit costs twice as many
    /// bytes as characters, and would be refused for being the wrong alphabet.
    /// </summary>
    [Test]
    public async Task A_multibyte_post_under_the_limit_is_accepted_although_its_bytes_are_over()
    {
        var bus = SendBus();

        // Cyrillic capital A repeated: two UTF-8 bytes each, so 3,000 characters is 6,000 bytes.
        var body = new string((char)0x0410, 3000);

        var result = await SendAsync(bus, body);

        Assert.Multiple(() =>
        {
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(body), Is.GreaterThan(PlanLimit),
                "the byte count really is over the ceiling, which is what makes this test worth having");
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
        });
    }

    /// <summary>
    /// The measured length is the plan ceiling, not the sum of the two arrays around it, so a long
    /// post is refused before it ever reaches auto-mod or consumes the author's slowmode window.
    /// </summary>
    [Test]
    public async Task A_refused_post_never_reaches_automod()
    {
        var bus = SendBus();

        await SendAsync(bus, new string('a', PlanLimit + 500));

        Assert.That(bus.Invoked.OfType<GetGuildAutoModConfigRequest>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ edit

    [Test]
    public async Task An_edit_that_grows_past_the_limit_is_refused_the_same_way()
    {
        var message = await SeedAsync();
        var bus = EditBus();

        var result = await new MessagingEndpoints().UpdateMessageAsync(
            message.Id,
            new UpdateMessageDto { Content = new string('a', PlanLimit + 1) },
            TestPrincipal.ForUser(UserId), bus, _repo, Lengths(bus));

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode,
                Is.EqualTo(MessageLengthPolicy.TooLongStatusCode),
                "create and edit answer with the same status");
            Assert.That(bus.Invoked.OfType<UpdateMessageCommand>(), Is.Empty);
        });
    }

    [Test]
    public async Task An_edit_to_exactly_the_limit_is_accepted()
    {
        var message = await SeedAsync();
        var bus = EditBus();

        var result = await new MessagingEndpoints().UpdateMessageAsync(
            message.Id,
            new UpdateMessageDto { Content = new string('a', PlanLimit) },
            TestPrincipal.ForUser(UserId), bus, _repo, Lengths(bus));

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(202));
            Assert.That(bus.Invoked.OfType<UpdateMessageCommand>(), Is.Not.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ helpers

    private static object? Read(object body, string property) =>
        body.GetType().GetProperty(property)!.GetValue(body);

    private async Task<Message> SeedAsync()
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hi"u8.ToArray(), ChannelId = ChannelId, AuthorId = UserId,
        });

        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return message;
    }

    private Task<IResult> SendAsync(FakeMessageBus bus, string content) =>
        new MessagingEndpoints().CreateMessage(
            new CreateMessageDto { Content = content, ChannelId = ChannelId },
            ScyllaContext.CreateDebug(), TestPrincipal.ForUser(UserId), _context, bus, _cache,
            new MlsGroupService(_context, new FakeMessagingHubContext(), bus,
                new MlsJoinRequestService(_context), TestMlsServices.Coverage(bus)),
            TestPrivacyServices.Build(bus).Policy,
            TestPrivacyServices.Build(bus).Content,
            Lengths(bus));

    /// <summary>A guild whose plan allows <see cref="PlanLimit"/> characters.</summary>
    private MessageLengthPolicy Lengths(FakeMessageBus bus) =>
        new(bus, _cache, NullLogger<MessageLengthPolicy>.Instance,
            new EntitlementResolver([new PlanSource()]));

    /// <summary>Everything the channel send path asks of Guild, with the send permitted.</summary>
    private static FakeMessageBus SendBus() => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
        {
            IsAllowed = true, Permission = r.Permission,
        },
        GetChannelRequest r => new GetChannelResponse
        {
            Channel = new ChannelInfo { Id = r.ChannelId, GuildId = GuildId, Name = "general", Type = "Text" },
        },
        GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
        ResolvePersonaForSendRequest => new ResolvePersonaForSendResponse { IsAllowed = true },
        CreateMessageCommand cmd => Message.Create(new CreateMessageParams
        {
            Content = cmd.Content, ChannelId = cmd.ChannelId, ConversationId = cmd.ConversationId,
            AuthorId = cmd.AuthorId,
        }),
        _ => throw new InvalidOperationException("unexpected " + msg.GetType().Name),
    });

    private static FakeMessageBus EditBus() => new(msg => msg switch
    {
        GetChannelRequest r => new GetChannelResponse
        {
            Channel = new ChannelInfo { Id = r.ChannelId, GuildId = GuildId, Name = "general", Type = "Text" },
        },
        UpdateMessageCommand => new UpdateMessageResponse { Success = true },
        _ => throw new InvalidOperationException("unexpected " + msg.GetType().Name),
    });

    /// <summary>One plan, on both sides, holding only the key under test.</summary>
    private sealed class PlanSource : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
                .Number(EntitlementKeys.GuildMessageMaxLength, PlanLimit, "free")
                .Number(EntitlementKeys.UserMessageMaxLength, PlanLimit, "free_user")
                .Build());
    }
}
