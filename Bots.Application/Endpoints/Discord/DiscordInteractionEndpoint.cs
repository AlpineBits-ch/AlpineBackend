using System.Text;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>
/// A bot's response to a slash command invocation. Deliberately NOT [Authorize]/bot-token-authed
/// - matches real Discord, where the interaction token itself (an unguessable secret handed to
/// the bot inside the INTERACTION_CREATE dispatch) is the only credential these endpoints need,
/// same as Discord's own webhook-execute model. Reuses the existing CreateMessageCommand bus
/// call for every response shape - no separate message-creation logic, just a new caller,
/// matching DiscordMessageEndpoint's own pattern.
/// </summary>
public class DiscordInteractionEndpoint
{
    /// <summary>Type 4 (CHANNEL_MESSAGE_WITH_SOURCE) posts the real message immediately. Type 5
    /// (DEFERRED_CHANNEL_MESSAGE_WITH_SOURCE) just acknowledges - the real message arrives later
    /// via the followup endpoint below, once the bot has actually finished its work.</summary>
    [WolverinePost("/api/discord/v10/interactions/{interactionId}/{token}/callback")]
    public async Task<IResult> CallbackAsync(string interactionId, string token, InteractionCallbackPayload callback,
        [NotBody] PendingInteractionStore pendingStore, [NotBody] IMessageBus bus)
    {
        var pending = await pendingStore.GetAsync(token);
        if (pending is null || pending.InteractionId != interactionId) return Results.NotFound();

        switch (callback.Type)
        {
            case 4:
                await CreateResponseMessageAsync(pending, callback.Data, bus);
                return Results.Ok();

            case 5:
                await pendingStore.MarkAcknowledgedAsync(token, pending);
                return Results.Ok();

            default:
                return Results.BadRequest($"Unsupported interaction callback type {callback.Type}.");
        }
    }

    [WolverinePost("/api/discord/v10/webhooks/{applicationId}/{token}")]
    public async Task<IResult> FollowupAsync(string applicationId, string token, InteractionResponseDataPayload body,
        [NotBody] PendingInteractionStore pendingStore, [NotBody] IMessageBus bus)
    {
        var pending = await pendingStore.GetAsync(token);
        if (pending is null || pending.BotUserId != applicationId) return Results.NotFound();

        var message = await CreateResponseMessageAsync(pending, body, bus);
        return Results.Ok(ToMessageResponseShape(message, pending));
    }

    /// <summary>Discord uses the literal path segment "@original" for the deferred response's
    /// placeholder. We never post a placeholder for a deferred interaction (nothing to show until
    /// the bot actually responds), so editing "@original" behaves the same as a followup: it
    /// posts the real message now, on whatever message id is passed.</summary>
    [WolverinePatch("/api/discord/v10/webhooks/{applicationId}/{token}/messages/{messageId}")]
    public async Task<IResult> EditFollowupAsync(string applicationId, string token, string messageId, InteractionResponseDataPayload body,
        [NotBody] PendingInteractionStore pendingStore, [NotBody] IMessageBus bus)
    {
        var pending = await pendingStore.GetAsync(token);
        if (pending is null || pending.BotUserId != applicationId) return Results.NotFound();

        var message = await CreateResponseMessageAsync(pending, body, bus);
        return Results.Ok(ToMessageResponseShape(message, pending));
    }

    private static async Task<Message> CreateResponseMessageAsync(PendingInteraction pending, InteractionResponseDataPayload? data, IMessageBus bus)
    {
        var embeds = data?.Embeds ?? new List<EmbedPayload>();
        var content = !string.IsNullOrEmpty(data?.Content) ? data.Content : EmbedFlattener.Flatten(null, embeds);

        return await bus.InvokeAsync<Message>(new CreateMessageCommand
        {
            AuthorId = pending.BotUserId,
            AuthorIdType = AuthorIdType.Bot,
            Content = Encoding.UTF8.GetBytes(content),
            ChannelId = pending.ChannelId,
            EmbedsJson = embeds.Count > 0 ? JsonSerializer.Serialize(embeds) : null,
        });
    }

    private static object ToMessageResponseShape(Message message, PendingInteraction pending) => new
    {
        id = message.Id,
        channel_id = pending.ChannelId,
        guild_id = pending.GuildId,
        content = Encoding.UTF8.GetString(message.Content),
        embeds = string.IsNullOrWhiteSpace(message.EmbedsJson)
            ? new List<EmbedPayload>()
            : JsonSerializer.Deserialize<List<EmbedPayload>>(message.EmbedsJson) ?? new List<EmbedPayload>(),
        timestamp = message.CreatedAt,
        author = new { id = pending.BotUserId, bot = true },
    };
}
