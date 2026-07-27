using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class ReactionCreatedForBotsHandler
{
    public static Task Handle(ReactionCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ReactionDispatchHelper.DispatchAsync(ctx, registry, "MESSAGE_REACTION_ADD", evt.GuildId, evt.ChannelId, evt.MessageId, evt.UserId, evt.Emoji);
}

public class ReactionRemovedForBotsHandler
{
    public static Task Handle(ReactionRemovedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ReactionDispatchHelper.DispatchAsync(ctx, registry, "MESSAGE_REACTION_REMOVE", evt.GuildId, evt.ChannelId, evt.MessageId, evt.UserId, evt.Emoji);
}

file static class ReactionDispatchHelper
{
    public static async Task DispatchAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        string eventName, string guildId, string channelId, string messageId, string userId, string emoji)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, guildId);
        if (botUserIds.Count == 0) return;

        var payload = new MessageReactionPayload
        {
            UserId = userId,
            ChannelId = channelId,
            MessageId = messageId,
            GuildId = guildId,
            Emoji = new EmojiPayload { Id = null, Name = emoji },
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, eventName, payload);
        }
    }
}
