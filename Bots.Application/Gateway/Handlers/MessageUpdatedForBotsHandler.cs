using System.Text;
using System.Text.Json;
using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Bots.Application.Gateway.Handlers;

/// <summary>
/// Translates Guild.Application's re-published MessageUpdatedForBots into a Gateway MESSAGE_UPDATE
/// dispatch for every bot installed in the guild - mirrors MessageCreatedForBotsHandler.
/// </summary>
public class MessageUpdatedForBotsHandler
{
    public static async Task Handle(MessageUpdatedForBots message, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IMessageBus bus, IBotChannelVisibility visibility)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsForChannelAsync(ctx, visibility, message.GuildId, message.ChannelId);
        if (botUserIds.Count == 0) return;

        var authorResponse = await bus.InvokeAsync<GetUserByIdResponse>(new GetUserByIdRequest { UserId = message.AuthorId });

        var payload = new MessageCreatePayload
        {
            Id = message.MessageId,
            ChannelId = message.ChannelId,
            GuildId = message.GuildId,
            Content = Encoding.UTF8.GetString(message.Content),
            Timestamp = DateTimeOffset.UtcNow,
            Embeds = DeserializeEmbeds(message.EmbedsJson),
            Flags = message.Flags,
            // Null when the author never edited the text.
            EditedTimestamp = message.EditedAt,
        };

        DiscordAuthorMapper.Apply(payload, new DiscordAuthorSource(
            message.AuthorIdType, message.AuthorId, message.AuthorDisplayName, message.AuthorAvatarUrl,
            message.PersonaId, authorResponse.User?.UserName, authorResponse.User?.IsBot ?? false));

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, "MESSAGE_UPDATE", payload);
        }
    }

    private static List<EmbedPayload> DeserializeEmbeds(string? embedsJson)
    {
        if (string.IsNullOrWhiteSpace(embedsJson)) return new List<EmbedPayload>();
        return JsonSerializer.Deserialize<List<EmbedPayload>>(embedsJson) ?? new List<EmbedPayload>();
    }
}
