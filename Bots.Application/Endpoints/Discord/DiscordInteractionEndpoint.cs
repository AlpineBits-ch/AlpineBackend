using System.Text;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Echo.Realtime;
using Ids;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
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
        [NotBody] PendingInteractionStore pendingStore, [NotBody] IMessageBus bus,
        [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var pending = await pendingStore.GetAsync(token);
        if (pending is null || pending.InteractionId != interactionId) return Results.NotFound();

        switch (callback.Type)
        {
            case InteractionCallbackType.ChannelMessageWithSource:
                await RespondAsync(pending, callback.Data, bus, pendingStore, hub);
                return Results.Ok();

            case InteractionCallbackType.DeferredChannelMessageWithSource:
            case InteractionCallbackType.DeferredUpdateMessage:
                // Both defers are pure acknowledgements. They differ only in what the *client*
                // shows while waiting ("thinking" vs nothing), which is the client's business -
                // on this side there is nothing to do but stop the interaction expiring.
                await pendingStore.MarkAcknowledgedAsync(token, pending);
                return Results.Ok();

            case InteractionCallbackType.UpdateMessage:
                if (pending.MessageId is null)
                    return Results.BadRequest("UPDATE_MESSAGE requires a component interaction - there is no originating message to update.");

                await UpdateOriginalMessageAsync(pending, callback.Data, bus);
                return Results.Ok();

            case InteractionCallbackType.AutocompleteResult:
                await pendingStore.SaveAutocompleteResultAsync(
                    pending.InteractionId,
                    JsonSerializer.Serialize(callback.Data?.Choices ?? []));
                return Results.Ok();

            case InteractionCallbackType.Modal:
                // A modal is pure UI: it never becomes a message. It goes straight to the one user
                // who triggered it and comes back later as a MODAL_SUBMIT interaction.
                await hub.Clients.User(pending.InvokingUserId).SendAsync("guild.ModalOpen", new
                {
                    pending.GuildId,
                    pending.ChannelId,
                    BotUserId = pending.BotUserId,
                    CustomId = callback.Data?.CustomId,
                    Title = callback.Data?.Title,
                    Components = callback.Data?.Components ?? [],
                });
                return Results.Ok();

            default:
                return Results.BadRequest($"Unsupported interaction callback type {callback.Type}.");
        }
    }

    /// <summary>Routes a type-4 response to either the normal message path or the ephemeral one.</summary>
    private static async Task RespondAsync(PendingInteraction pending, InteractionResponseDataPayload? data,
        IMessageBus bus, PendingInteractionStore pendingStore, IHubContext<EchoRealtimeHub> hub)
    {
        if (IsEphemeral(data))
        {
            await SendEphemeralAsync(pending, data!, pendingStore, hub);
            return;
        }

        await CreateResponseMessageAsync(pending, data, bus);
    }

    /// <summary>Flag 64 = EPHEMERAL.</summary>
    private static bool IsEphemeral(InteractionResponseDataPayload? data) => ((data?.Flags ?? 0) & 64) != 0;

    /// <summary>
    /// Delivers an ephemeral response: pushed over the realtime hub to the invoking user alone and
    /// never written to the message store.
    ///
    /// Not persisting is the whole design, not a shortcut. An ephemeral reply that lived in the
    /// message table would have to be filtered out of history reads, search indexing, unread
    /// counts, bulk delete, the bots' MESSAGE_CREATE dispatch and every export - one missed filter
    /// leaks a private reply into a public channel. Keeping it off disk means there is nothing to
    /// filter, and it matches what users already expect from Discord: ephemeral messages vanish on
    /// reload.
    /// </summary>
    private static async Task SendEphemeralAsync(PendingInteraction pending, InteractionResponseDataPayload data,
        PendingInteractionStore pendingStore, IHubContext<EchoRealtimeHub> hub)
    {
        var embeds = data.Embeds;
        var content = !string.IsNullOrEmpty(data.Content) ? data.Content : EmbedFlattener.Flatten(null, embeds);
        var ephemeralId = Identifier.New("ephm");

        var customIds = data.Components.SelectMany(c => c.CollectCustomIds()).ToList();
        if (customIds.Count > 0)
        {
            await pendingStore.SaveEphemeralAsync(new EphemeralMessageRecord(
                ephemeralId, pending.BotUserId, pending.GuildId, pending.ChannelId, pending.InvokingUserId, customIds));
        }

        await hub.Clients.User(pending.InvokingUserId).SendAsync("guild.EphemeralMessageCreated", new
        {
            Id = ephemeralId,
            pending.GuildId,
            pending.ChannelId,
            Content = content,
            Embeds = embeds,
            Components = data.Components,
            AuthorId = pending.BotUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task UpdateOriginalMessageAsync(PendingInteraction pending, InteractionResponseDataPayload? data, IMessageBus bus)
    {
        var embeds = data?.Embeds ?? [];
        var content = !string.IsNullOrEmpty(data?.Content) ? data.Content : EmbedFlattener.Flatten(null, embeds);

        await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = pending.MessageId!,
            // The bot owns the message it is updating; the human who clicked is not its author.
            RequestingAuthorId = pending.BotUserId,
            AllowBotAuthorEdit = true,
            Content = Encoding.UTF8.GetBytes(content),
            EmbedsJson = embeds.Count > 0 ? JsonSerializer.Serialize(embeds) : null,
            // Always written on an update, including as an empty array - "disable the buttons now
            // that this flow is done" is the single most common thing an UPDATE_MESSAGE does.
            ComponentsJson = JsonSerializer.Serialize(data?.Components ?? []),
        });
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
            ComponentsJson = data?.Components.Count > 0 ? JsonSerializer.Serialize(data.Components) : null,
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
