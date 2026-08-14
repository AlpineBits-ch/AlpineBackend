using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bots.Application.Endpoints;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints;

/// <summary>
/// Covers ComponentInteractionEndpoint - the venta client's side of buttons, selects and modals.
/// </summary>
[TestFixture]
public class ComponentInteractionEndpointTests
{
    private const string GuildId = "gld_1";
    private const string ChannelId = "ch_1";
    private const string MessageId = "mesg_1";
    private const string BotUserId = "usr_bot1";
    private const string UserId = "usr_invoker";

    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;
    private PendingInteractionStore _pendingStore = null!;
    private GatewayConnectionRegistry _registry = null!;
    private FakeSubscriber _subscriber = null!;
    private ComponentInteractionEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
        _pendingStore = new PendingInteractionStore(new FakeDistributedCache());
        (_registry, _subscriber) = GatewayRegistryTestFactory.Create();
        _endpoint = new ComponentInteractionEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task InstallBotAsync()
    {
        var app = new BotApplication
        {
            Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = BotUserId,
            Name = "Test Bot", IsEnabled = true,
        };
        _context.BotApplications.Add(app);
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = GuildId,
            InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>Canned "the message exists and carries one button with this custom_id".</summary>
    private void GivenMessageWithButton(string customId)
    {
        var components = new List<ComponentPayload>
        {
            new()
            {
                Type = ComponentType.ActionRow,
                Components = [new ComponentPayload { Type = ComponentType.Button, CustomId = customId }],
            },
        };

        _bus.MessageResponse = new GetMessageResponse
        {
            Message = new MessageSummary
            {
                Id = MessageId,
                AuthorId = BotUserId,
                ChannelId = ChannelId,
                Content = "Pick one"u8.ToArray(),
                ComponentsJson = JsonSerializer.Serialize(components),
            },
        };
    }

    private Task<IResult> Invoke(string customId, string messageId = MessageId, string userId = UserId) =>
        _endpoint.InvokeComponentAsync(GuildId, ChannelId, messageId,
            new InvokeComponentDto { CustomId = customId }, MakeUser(userId), _context, _bus, _registry, _pendingStore);

    // ── InvokeComponentAsync ─────────────────────────────────────────────────

    [Test]
    public async Task Invoke_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.InvokeComponentAsync(GuildId, ChannelId, MessageId,
            new InvokeComponentDto { CustomId = "x" },
            new ClaimsPrincipal(new ClaimsIdentity()), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Invoke_MissingCustomId_ReturnsBadRequest()
    {
        var result = await Invoke("");
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Invoke_LacksSendMessages_ReturnsForbid()
    {
        _bus.PermissionResponse = new HasUserPermissionToChannelResponse
        {
            IsAllowed = false, Permission = ExternalPermission.SendMessages,
        };

        var result = await Invoke("confirm");

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>(),
            "pressing a button is participation, so it takes the same permission as speaking");
    }

    [Test]
    public async Task Invoke_LacksUseApplicationCommands_ReturnsForbid()
    {
        await InstallBotAsync();
        GivenMessageWithButton("confirm");
        _bus.ChannelPermissionOverrides[ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[ExternalPermission.UseApplicationCommands] = false;

        var result = await Invoke("confirm");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>(),
                "a member denied the bot gate must not reach by button what they were refused by name");
            Assert.That(_subscriber.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task Invoke_HoldsBothPermissions_Dispatches()
    {
        await InstallBotAsync();
        GivenMessageWithButton("confirm");
        _bus.ChannelPermissionOverrides[ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[ExternalPermission.UseApplicationCommands] = true;

        var result = await Invoke("confirm");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(_subscriber.Messages, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task SubmitModal_LacksUseApplicationCommands_ReturnsForbid()
    {
        await InstallBotAsync();
        _bus.ChannelPermissionOverrides[ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[ExternalPermission.UseApplicationCommands] = false;

        var result = await _endpoint.SubmitModalAsync(GuildId, ChannelId,
            new SubmitModalDto { BotUserId = BotUserId, CustomId = "form" },
            MakeUser(UserId), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Autocomplete_LacksUseApplicationCommands_ReturnsForbid()
    {
        await InstallBotAsync();
        _bus.ChannelPermissionOverrides[ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[ExternalPermission.UseApplicationCommands] = false;

        var result = await _endpoint.AutocompleteAsync(GuildId, ChannelId,
            new AutocompleteRequestDto { BotUserId = BotUserId, CommandName = "ping" },
            MakeUser(UserId), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Invoke_UnknownMessage_ReturnsNotFound()
    {
        await InstallBotAsync();
        _bus.MessageResponse = new GetMessageResponse { Message = null };

        var result = await Invoke("confirm");

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Invoke_MessageLookupNamesThePressingUser()
    {
        // Bots has no message store, so this fetch is the only way it can see the components it is
        // about to validate against.
        await InstallBotAsync();
        GivenMessageWithButton("confirm");

        await Invoke("confirm");

        var request = _bus.Invoked.OfType<GetMessageRequest>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.RequestingUserId, Is.EqualTo(UserId));
            Assert.That(request.MessageId, Is.EqualTo(MessageId));
            Assert.That(request.Scope, Is.EqualTo(MessageReadScope.Full),
                "it reads the body and the components, so it asks the full-scope question");
        });
    }

    [Test]
    public async Task Invoke_CustomIdNotOnTheMessage_ReturnsBadRequest()
    {
        await InstallBotAsync();
        GivenMessageWithButton("confirm");

        var result = await Invoke("i-made-this-up");

        Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
            "without this check any client could drive a bot into a path no visible button reaches");
    }

    [Test]
    public async Task Invoke_MessageInAnotherChannel_ReturnsNotFound()
    {
        await InstallBotAsync();
        GivenMessageWithButton("confirm");
        _bus.MessageResponse.Message!.ChannelId = "ch_other";

        var result = await Invoke("confirm");

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Invoke_BotNotInstalled_ReturnsNotFound()
    {
        GivenMessageWithButton("confirm");

        var result = await Invoke("confirm");

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task Invoke_Valid_DispatchesAMessageComponentInteractionCarryingTheMessage()
    {
        await InstallBotAsync();
        GivenMessageWithButton("confirm");

        var result = await Invoke("confirm");

        Assert.That(result, Is.InstanceOf<Accepted>());

        var published = JsonDocument.Parse(_subscriber.Messages.Single().Message.ToString()).RootElement;
        var interaction = published.GetProperty("Data");

        Assert.Multiple(() =>
        {
            Assert.That(published.GetProperty("EventName").GetString(), Is.EqualTo("INTERACTION_CREATE"));
            Assert.That(interaction.GetProperty("type").GetInt32(), Is.EqualTo(InteractionType.MessageComponent));
            Assert.That(interaction.GetProperty("data").GetProperty("custom_id").GetString(), Is.EqualTo("confirm"));
            Assert.That(interaction.GetProperty("message").GetProperty("id").GetString(), Is.EqualTo(MessageId),
                "UPDATE_MESSAGE needs the originating message, so it has to travel with the interaction");
        });
    }

    // ── Ephemeral messages ───────────────────────────────────────────────────

    [Test]
    public async Task Invoke_EphemeralMessage_BelongingToAnotherUser_ReturnsNotFound()
    {
        await InstallBotAsync();
        await _pendingStore.SaveEphemeralAsync(new EphemeralMessageRecord(
            "ephm_1", BotUserId, GuildId, ChannelId, InvokingUserId: "someone-else", CustomIds: ["btn"]));

        var result = await Invoke("btn", messageId: "ephm_1", userId: UserId);

        Assert.That(result, Is.InstanceOf<NotFound>(),
            "an ephemeral message is addressed to exactly one user and nobody else may act on it");
    }

    [Test]
    public async Task Invoke_EphemeralMessage_UnknownCustomId_ReturnsBadRequest()
    {
        await InstallBotAsync();
        await _pendingStore.SaveEphemeralAsync(new EphemeralMessageRecord(
            "ephm_1", BotUserId, GuildId, ChannelId, UserId, CustomIds: ["btn"]));

        var result = await Invoke("not-a-button", messageId: "ephm_1");

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Invoke_EphemeralMessage_Valid_Dispatches()
    {
        await InstallBotAsync();
        await _pendingStore.SaveEphemeralAsync(new EphemeralMessageRecord(
            "ephm_1", BotUserId, GuildId, ChannelId, UserId, CustomIds: ["btn"]));

        var result = await Invoke("btn", messageId: "ephm_1");

        Assert.That(result, Is.InstanceOf<Accepted>());
    }

    [Test]
    public async Task Invoke_EphemeralMessage_Expired_ReturnsNotFound()
    {
        await InstallBotAsync();

        // Nothing saved: an expired ephemeral is indistinguishable from one that never existed,
        // which is the correct outcome - the interaction token behind it has expired too.
        var result = await Invoke("btn", messageId: "ephm_gone");

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── SubmitModalAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task SubmitModal_MissingCustomId_ReturnsBadRequest()
    {
        var result = await _endpoint.SubmitModalAsync(GuildId, ChannelId,
            new SubmitModalDto { BotUserId = BotUserId, CustomId = "" },
            MakeUser(UserId), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SubmitModal_BotNotInstalled_ReturnsNotFound()
    {
        var result = await _endpoint.SubmitModalAsync(GuildId, ChannelId,
            new SubmitModalDto { BotUserId = BotUserId, CustomId = "feedback" },
            MakeUser(UserId), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task SubmitModal_Valid_Dispatches()
    {
        await InstallBotAsync();

        var result = await _endpoint.SubmitModalAsync(GuildId, ChannelId,
            new SubmitModalDto
            {
                BotUserId = BotUserId,
                CustomId = "feedback",
                Components =
                [
                    new ComponentPayload
                    {
                        Type = ComponentType.ActionRow,
                        Components = [new ComponentPayload { Type = ComponentType.TextInput, CustomId = "body", Value = "it works" }],
                    },
                ],
            },
            MakeUser(UserId), _context, _bus, _registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<Accepted>());
    }

    // ── Component payload helper ─────────────────────────────────────────────

    [Test]
    public void CollectCustomIds_WalksNestedActionRows()
    {
        var row = new ComponentPayload
        {
            Type = ComponentType.ActionRow,
            Components =
            [
                new ComponentPayload { Type = ComponentType.Button, CustomId = "a" },
                new ComponentPayload { Type = ComponentType.Button, CustomId = "b" },
                // A link button has no custom_id and must not contribute an empty entry.
                new ComponentPayload { Type = ComponentType.Button, Style = 5, Url = "https://example.test" },
            ],
        };

        Assert.That(row.CollectCustomIds(), Is.EquivalentTo(new[] { "a", "b" }));
    }
}
