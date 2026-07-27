using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

/// <summary>Discord has a single GUILD_MEMBER_REMOVE event for ban/kick/leave alike - Reason on
/// the source event is informational only and doesn't change the dispatch shape.</summary>
public class MemberRemovedForBotsHandler
{
    public static async Task Handle(MemberRemovedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, evt.GuildId);
        if (botUserIds.Count == 0) return;

        var payload = new GuildMemberRemovePayload
        {
            GuildId = evt.GuildId,
            User = new DiscordUserPayload { Id = evt.UserId, Username = evt.UserId, Bot = false },
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, "GUILD_MEMBER_REMOVE", payload);
        }
    }
}
