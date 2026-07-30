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
    private DiscordInteractionEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _pendingStore = new PendingInteractionStore(new FakeDistributedCache());
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
        var result = await _endpoint.CallbackAsync("intr_1", "never-saved-token", new InteractionCallbackPayload { Type = 4 }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Callback_TokenBelongsToDifferentInteraction_ReturnsNotFound()
    {
        await SavePendingAsync("token-abc", interactionId: "intr_other");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 4 }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Callback_Type4_PostsRealMessageImmediately()
    {
        await SavePendingAsync("token-abc");

        var callback = new InteractionCallbackPayload { Type = 4, Data = new InteractionResponseDataPayload { Content = "pong" } };
        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", callback, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<Ok>());
        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(command.ChannelId, Is.EqualTo("ch_1"));
    }

    [Test]
    public async Task Callback_Type5_AcknowledgesWithoutPostingAMessage()
    {
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 5 }, _pendingStore, _bus);

        Assert.That(result, Is.InstanceOf<Ok>());
        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);

        var pending = await _pendingStore.GetAsync("token-abc");
        Assert.That(pending!.Acknowledged, Is.True);
    }

    [Test]
    public async Task Callback_UnsupportedType_ReturnsBadRequest()
    {
        await SavePendingAsync("token-abc");

        var result = await _endpoint.CallbackAsync("intr_1", "token-abc", new InteractionCallbackPayload { Type = 99 }, _pendingStore, _bus);

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
}
