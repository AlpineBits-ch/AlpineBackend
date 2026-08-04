using System.Text.Json;
using Bots.Application.Endpoints.Discord;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Tests.Helpers;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordInteractionEndpointTests
{
    private FakeMessagingBus _bus = null!;
    private PendingInteractionStore _pendingStore = null!;
    private FakeBotsHubContext _hub = null!;
    private DiscordInteractionEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _pendingStore = new PendingInteractionStore(new FakeDistributedCache());
        _hub = new FakeBotsHubContext();
        _endpoint = new DiscordInteractionEndpoint();
    }

    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private Task SavePendingAsync(string token, string interactionId = "intr_1", string botUserId = "usr_bot1",
        string? guildId = "gld_1", string channelId = "ch_1", bool acknowledged = false) =>
        _pendingStore.SaveAsync(token, new PendingInteraction(interactionId, botUserId, guildId, channelId, "usr_invoker", "ping", acknowledged));

    // ── CallbackAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task Callback_UnknownToken_ReturnsNotFound()
    {
        var result = await _endpoint.CallbackAsync("intr_1", "never-saved-token", new InteractionCallbackPayload { Type = 4 }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Callback_TokenBelongsToDifferentInteraction_ReturnsNotFound()
    {
        await SavePendingAsync("token-abc", interactionId: "intr_other");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 4 }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Callback_Type4_PostsRealMessageImmediately()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload { Type = 4, Data = new InteractionResponseDataPayload { Content = "pong" } };
        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<Ok>());
        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(command.ChannelId, Is.EqualTo("ch_1"));
    }

    [Test]
    public async Task Callback_Type5_AcknowledgesWithoutPostingAMessage()
    {
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 5 }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);

        var pending = await _pendingStore.GetAsync("token-abc");
        Assert.That(pending!.Acknowledged, Is.True);
    }

    [Test]
    public async Task Callback_UnsupportedType_ReturnsBadRequest()
    {
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 99 }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ── FollowupAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task Followup_UnknownToken_ReturnsNotFound()
    {
        var result = await _endpoint.FollowupAsync("usr_bot1", "never-saved-token", new InteractionResponseDataPayload { Content = "hi" }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Followup_ApplicationIdMismatch_ReturnsNotFound()
    {
        await SavePendingAsync("token-abc", botUserId: "usr_bot1");

        var result = await _endpoint.FollowupAsync("usr_bot2", "token-abc", new InteractionResponseDataPayload { Content = "hi" }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Followup_ValidToken_PostsMessageAndReturnsMessageShapeFromPendingContext()
    {
        await SavePendingAsync("token-abc", botUserId: "usr_bot1", guildId: "gld_1", channelId: "ch_1");

        var result = await _endpoint.FollowupAsync("usr_bot1", "token-abc", new InteractionResponseDataPayload { Content = "followup text" }, _pendingStore, _bus);

        // The response shape is built from the pending interaction's context (channel/guild/author),
        // not from re-echoing the request body - assert on that, not on the canned Message content.
        var value = GetValue(result);
        var channelId = (string)value.GetType().GetProperty("channel_id")!.GetValue(value)!;
        var guildId = (string?)value.GetType().GetProperty("guild_id")!.GetValue(value);
        var author = value.GetType().GetProperty("author")!.GetValue(value)!;
        var authorId = (string)author.GetType().GetProperty("id")!.GetValue(author)!;
        Assert.Multiple(() =>
        {
            Assert.That(channelId, Is.EqualTo("ch_1"));
            Assert.That(guildId, Is.EqualTo("gld_1"));
            Assert.That(authorId, Is.EqualTo("usr_bot1"));
        });

        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(System.Text.Encoding.UTF8.GetString(command.Content), Is.EqualTo("followup text"));
    }

    [Test]
    public async Task Followup_EmptyContentWithEmbeds_FlattensEmbedsIntoContent()
    {
        await SavePendingAsync("token-abc");

        var body = new InteractionResponseDataPayload { Content = null, Embeds = [new EmbedPayload { Title = "Embed Title" }] };
        await _endpoint.FollowupAsync("usr_bot1", "token-abc", body, _pendingStore, _bus);

        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(command.EmbedsJson, Does.Contain("Embed Title"));
    }

    // ── EditFollowupAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task EditFollowup_ApplicationIdMismatch_ReturnsNotFound()
    {
        await SavePendingAsync("token-abc", botUserId: "usr_bot1");

        var result = await _endpoint.EditFollowupAsync("usr_bot2", "token-abc", "msg_1", new InteractionResponseDataPayload { Content = "edited" }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task EditFollowup_ValidToken_PostsNewMessage()
    {
        await SavePendingAsync("token-abc", botUserId: "usr_bot1");

        var result = await _endpoint.EditFollowupAsync("usr_bot1", "token-abc", "msg_1", new InteractionResponseDataPayload { Content = "edited" }, _pendingStore, _bus);

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Not.Empty);
        Assert.That(GetValue(result), Is.Not.Null);
    }

    // ── Components on a response ──────────────────────────────────────────────

    [Test]
    public async Task Callback_WithComponents_PersistsThemAsJson()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.ChannelMessageWithSource,
            Data = new InteractionResponseDataPayload
            {
                Content = "Pick one",
                Components =
                [
                    new ComponentPayload
                    {
                        Type = ComponentType.ActionRow,
                        Components =
                        [
                            new ComponentPayload { Type = ComponentType.Button, CustomId = "confirm", Label = "Confirm", Style = 1 },
                        ],
                    },
                ],
            },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(command.ComponentsJson, Does.Contain("confirm"));
    }

    // ── Ephemeral (flag 64) ───────────────────────────────────────────────────

    [Test]
    public async Task Callback_Ephemeral_IsNeverWrittenToTheMessageStore()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.ChannelMessageWithSource,
            Data = new InteractionResponseDataPayload { Content = "only you", Flags = 64 },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty,
            "persisting an ephemeral reply would put a private message into channel history");
    }

    [Test]
    public async Task Callback_Ephemeral_GoesOnlyToTheInvokingUser()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.ChannelMessageWithSource,
            Data = new InteractionResponseDataPayload { Content = "only you", Flags = 64 },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var send = _hub.HubClients.Sent.Single();
        Assert.Multiple(() =>
        {
            Assert.That(send.Method, Is.EqualTo("guild.EphemeralMessageCreated"));
            Assert.That(send.UserId, Is.EqualTo("usr_invoker"));
        });
    }

    [Test]
    public async Task Callback_EphemeralWithComponents_RegistersThemForLaterValidation()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.ChannelMessageWithSource,
            Data = new InteractionResponseDataPayload
            {
                Content = "only you",
                Flags = 64,
                Components =
                [
                    new ComponentPayload
                    {
                        Type = ComponentType.ActionRow,
                        Components = [new ComponentPayload { Type = ComponentType.Button, CustomId = "ephemeral-btn" }],
                    },
                ],
            },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        // The ephemeral message has no row to validate a later click against, so its custom_ids
        // are parked in Redis instead - without this the button would be unusable.
        var send = _hub.HubClients.Sent.Single();
        var ephemeralId = send.Args[0]!.GetType().GetProperty("Id")!.GetValue(send.Args[0])!.ToString()!;

        var record = await _pendingStore.GetEphemeralAsync(ephemeralId);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.CustomIds, Does.Contain("ephemeral-btn"));
    }

    // ── UPDATE_MESSAGE (type 7) ───────────────────────────────────────────────

    [Test]
    public async Task Callback_UpdateMessage_WithoutAnOriginatingMessage_ReturnsBadRequest()
    {
        // A slash command interaction has no message to update.
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc",
            new InteractionCallbackPayload { Type = InteractionCallbackType.UpdateMessage }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Callback_UpdateMessage_EditsTheOriginalAndMayClearComponents()
    {
        await _pendingStore.SaveAsync("token-abc", new PendingInteraction(
            "intr_1", "usr_bot1", "gld_1", "ch_1", "usr_invoker", "confirm", Acknowledged: false,
            MessageId: "mesg_1", InteractionType: InteractionType.MessageComponent));

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.UpdateMessage,
            Data = new InteractionResponseDataPayload { Content = "Confirmed.", Components = [] },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var update = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.MessageId, Is.EqualTo("mesg_1"));
            Assert.That(update.AllowBotAuthorEdit, Is.True,
                "the clicking human is not the message author, so the ordinary author check cannot apply");
            Assert.That(update.ComponentsJson, Is.EqualTo("[]"),
                "an empty array is how 'disable the buttons now the flow is done' is expressed");
        });
    }

    /// <summary>
    /// The commonest UPDATE_MESSAGE of all - "the flow is done, grey the buttons out" - mentions
    /// neither content nor embeds. It used to arrive as content "" and embeds null, which blanked
    /// the message it was only meant to disable the buttons on.
    /// </summary>
    [Test]
    public async Task Callback_UpdateMessage_ComponentsOnly_LeavesContentAndEmbedsAlone()
    {
        await _pendingStore.SaveAsync("token-abc", new PendingInteraction(
            "intr_1", "usr_bot1", "gld_1", "ch_1", "usr_invoker", "confirm", Acknowledged: false,
            MessageId: "mesg_1", InteractionType: InteractionType.MessageComponent));

        // Deserialized rather than constructed: "the bot did not send this key" is a property of
        // the wire body, and an object initializer cannot express it.
        var callback = JsonSerializer.Deserialize<InteractionCallbackPayload>(
            """{"type":7,"data":{"components":[]}}""", JsonSerializerOptions.Web)!;

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var update = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.Content, Is.Null, "no content key means the text stays as it is");
            Assert.That(update.EmbedsJson, Is.Null, "no embeds key means the embeds stay as they are");
            Assert.That(update.ComponentsJson, Is.EqualTo("[]"));
        });
    }

    [Test]
    public async Task Callback_UpdateMessage_WithEmbeds_ReplacesThem()
    {
        await _pendingStore.SaveAsync("token-abc", new PendingInteraction(
            "intr_1", "usr_bot1", "gld_1", "ch_1", "usr_invoker", "confirm", Acknowledged: false,
            MessageId: "mesg_1", InteractionType: InteractionType.MessageComponent));

        var callback = JsonSerializer.Deserialize<InteractionCallbackPayload>(
            """{"type":7,"data":{"embeds":[{"title":"Updated Card"}]}}""", JsonSerializerOptions.Web)!;

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var update = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.EmbedsJson, Does.Contain("Updated Card"));
            Assert.That(update.Content, Is.Null, "an embeds-only update must not overwrite the bot's text");
        });
    }

    // ── Modal (type 9) ────────────────────────────────────────────────────────

    [Test]
    public async Task Callback_Modal_IsPushedToTheInvokerAndNeverBecomesAMessage()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.Modal,
            Data = new InteractionResponseDataPayload { CustomId = "feedback-modal", Title = "Feedback" },
        };

        await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus, _hub);

        var send = _hub.HubClients.Sent.Single();
        Assert.Multiple(() =>
        {
            Assert.That(send.Method, Is.EqualTo("guild.ModalOpen"));
            Assert.That(send.UserId, Is.EqualTo("usr_invoker"));
            Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);
        });
    }

    // ── Autocomplete (type 8) ─────────────────────────────────────────────────

    [Test]
    public async Task Callback_AutocompleteResult_IsParkedForThePollingRequest()
    {
        await SavePendingAsync("token-abc", interactionId: "intr_auto");

        var callback = new InteractionCallbackPayload
        {
            Type = InteractionCallbackType.AutocompleteResult,
            Data = new InteractionResponseDataPayload
            {
                Choices = [new AutocompleteChoicePayload { Name = "Berlin" }],
            },
        };

        await _endpoint.CallbackAsync("intr_auto", "token-abc", callback, _pendingStore, _bus, _hub);

        var stored = await _pendingStore.GetAutocompleteResultAsync("intr_auto");
        Assert.That(stored, Does.Contain("Berlin"));
    }

    [Test]
    public async Task Callback_DeferredUpdateMessage_IsAcknowledgedLikeTheOtherDefer()
    {
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc",
            new InteractionCallbackPayload { Type = InteractionCallbackType.DeferredUpdateMessage }, _pendingStore, _bus, _hub);

        Assert.That(result, Is.InstanceOf<Ok>());
        var pending = await _pendingStore.GetAsync("token-abc");
        Assert.That(pending!.Acknowledged, Is.True);
    }
}
