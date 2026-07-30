using System.Text;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Echo.Realtime;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Persistence;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>A bot's response to a slash command invocation.</summary>
public class DiscordInteractionEndpoint
{
    /// <summary>Type 4 (CHANNEL_MESSAGE_WITH_SOURCE) posts the real message immediately.</summary>
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
                // Both defers are pure acknowledgements.
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
                // A modal is pure UI: it never becomes a message.
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
    /// </summary>
    private static async Task SendEphemeralAsync(PendingInteraction pending, InteractionResponseDataPayload data,
        PendingInteractionStore pendingStore, IHubContext<EchoRealtimeHub> hub)
    {
        var embeds = data.Embeds;
        var content = !string.IsNullOrEmpty(data.Content) ? data.Content : EmbedFlattener.Flatten(null, embeds);
        var ephemeralId = Ksuid.Generate("ephm_");

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

    /// <summary>
    /// Discord uses the literal path segment "@original" for the deferred response's placeholder.
    /// </summary>
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
