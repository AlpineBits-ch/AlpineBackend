using System.Text;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Messages;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Previews;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Domain;
using CommandMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Messaging.Tests.Handlers;

/// <summary>
/// The durable half of a voice ring: the card left in the two people's conversation, which is the
/// only surface still there once the ring itself has lapsed a minute later.
/// </summary>
[TestFixture]
public class VoiceRingDirectMessageRequestedHandlerTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-voice";
    private const string Inviter = "user-inviter";
    private const string Target = "user-target";

    private static readonly DateTimeOffset Expiry = new(2026, 8, 15, 12, 1, 0, TimeSpan.Zero);

    private TestMessagingContext _context = null!;
    private FakeMessageBus _bus = null!;
    private readonly Dictionary<string, ProfileDto> _byUserId = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _byUserId.Clear();

        foreach (var id in new[] { Inviter, Target })
            _byUserId[id] = new ProfileDto { UserId = id, UserName = id, Hash = 42 };

        _bus = new FakeMessageBus(message => message switch
        {
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse
            {
                Profile = _byUserId.GetValueOrDefault(r.UserId),
            },
            _ => throw new InvalidOperationException($"No responder for {message.GetType().Name}"),
        });
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ Arrangement
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<string> SeedConversationAsync()
    {
        var id = Conversation.GenerateId();
        _context.Conversations.Add(new Conversation
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EncryptionState = ChannelEncryptionState.Plain,
            Members = new[] { Inviter, Target }.Select(u => new ConversationMember
            {
                Id = ConversationMember.GenerateId(),
                ConversationId = id,
                UserId = u,
                PublicKey = [],
                CachedUserName = u,
                CachedUserHash = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ToList(),
        });

        await _context.SaveChangesAsync();
        return id;
    }

    private static VoiceRingDirectMessageRequested Request(string channelName = "General") => new()
    {
        RingId = "vrng-1",
        GuildId = GuildId,
        ChannelId = ChannelId,
        ChannelName = channelName,
        InviterId = Inviter,
        TargetUserId = Target,
        ExpiresAt = Expiry,
    };

    private async Task HandleAsync(VoiceRingDirectMessageRequested? request = null)
    {
        var privacy = TestPrivacyServices.Build(_bus);
        var resolver = new DirectConversationResolver(
            _context, privacy.Policy, privacy.Bus, NullLogger<DirectConversationResolver>.Instance);

        await VoiceRingDirectMessageRequestedHandler.Handle(
            request ?? Request(), resolver, _bus,
            NullLogger<VoiceRingDirectMessageRequestedHandler>.Instance);
    }

    private CreateMessageCommand? Written() =>
        _bus.Invoked.OfType<CreateMessageCommand>().SingleOrDefault();

    private static EmbedPayload Card(CreateMessageCommand command) =>
        GeneratedEmbeds.Parse(command.EmbedsJson).Single();

    // ══════════════════════════════════════════════════════════════════════════ The message
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Writes_TheInvitationIntoTheExistingConversation()
    {
        var conversationId = await SeedConversationAsync();

        await HandleAsync();

        var written = Written();
        Assert.That(written, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(written!.ConversationId, Is.EqualTo(conversationId));
            Assert.That(written.Type, Is.EqualTo(CommandMessageType.VoiceChannelInvite));
            Assert.That(written.AuthorId, Is.EqualTo(Inviter),
                "authorship is what lets a client render 'X asked you to join' with no second lookup");
        });
    }

    [Test]
    public async Task Writes_APlainEnglishFallbackIntoContent()
    {
        await SeedConversationAsync();

        await HandleAsync();

        Assert.That(Encoding.UTF8.GetString(Written()!.Content), Is.EqualTo("Asked you to join General"),
            "bots, exports and search see Content and nothing else");
    }

    [Test]
    public async Task StartsAConversation_WhenTheTwoHaveNoneYet()
    {
        _byUserId[Target].Relationships =
            [new RelationshipDto { UserId = Inviter, Status = RelationshipStatus.Accepted }];

        await HandleAsync();

        Assert.That(Written()?.ConversationId, Is.Not.Null.And.EqualTo(_context.Conversations.Single().Id));
    }

    [Test]
    public async Task WritesNothing_WhenNoConversationCanBeOpened()
    {
        // Not friends, and the product default is friends-only, so the resolver refuses to start
        // one. The invitation is not lost - the realtime card and the push both went out already.
        await HandleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Written(), Is.Null);
            Assert.That(_context.Conversations.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task WritesNothing_WhenTheEventNamesNobody()
    {
        var request = Request();
        request.TargetUserId = "";

        await HandleAsync(request);

        Assert.That(Written(), Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ The card
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Card_CarriesEverythingNeededToJoin()
    {
        await SeedConversationAsync();

        await HandleAsync();
        var card = Card(Written()!);

        Assert.Multiple(() =>
        {
            Assert.That(card.Type, Is.EqualTo(EmbedTypes.VentaVoiceInvite));
            Assert.That(card.Title, Is.EqualTo("General"));
            Assert.That(card.Venta, Is.Not.Null);
            Assert.That(card.Venta!.Kind, Is.EqualTo("voice_invite"));
            Assert.That(card.Venta.RingId, Is.EqualTo("vrng-1"));
            Assert.That(card.Venta.GuildId, Is.EqualTo(GuildId));
            Assert.That(card.Venta.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(card.Venta.ChannelName, Is.EqualTo("General"));
            Assert.That(card.Venta.InviterId, Is.EqualTo(Inviter));
            Assert.That(card.Venta.ExpiresAt, Is.EqualTo(Expiry),
                "an absolute instant does not go stale the way 'expired' does");
        });
    }

    [Test]
    public async Task Card_IsMarkedServerGenerated()
    {
        await SeedConversationAsync();

        await HandleAsync();

        Assert.That(Card(Written()!).IsGenerated, Is.True,
            "a client that acts on venta identifiers without checking this flag is trusting a bot author");
    }

    [Test]
    public async Task Card_CarriesNoUrl()
    {
        await SeedConversationAsync();

        await HandleAsync();

        Assert.That(Card(Written()!).Url, Is.Null,
            "there is no link shape for a channel, and inventing one would mint an unrevocable way in");
    }

    [Test]
    public async Task Card_SurvivesARoundTripThroughTheStoredJson()
    {
        await SeedConversationAsync();

        await HandleAsync();

        // Parsed back with the same options the read paths use, because the venta block is
        // snake_cased and a casing mismatch would deserialize every identifier to null in silence.
        var reparsed = GeneratedEmbeds.Parse(GeneratedEmbeds.Serialize([Card(Written()!)])).Single();

        Assert.That(reparsed.Venta?.ChannelId, Is.EqualTo(ChannelId));
    }
}
