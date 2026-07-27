using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class MessageDeletedForBotsHandler
{
    public static async Task Handle(MessageDeletedForBots message, MicroserviceContext ctx, GatewayConnectionRegistry registry)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, message.GuildId);
        if (botUserIds.Count == 0) return;

        var payload = new MessageDeletePayload
        {
            Id = message.MessageId,
            ChannelId = message.ChannelId,
            GuildId = message.GuildId,
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, "MESSAGE_DELETE", payload);
        }
    }
}
