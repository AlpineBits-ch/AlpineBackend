using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Bots.Application.Gateway.Handlers;

public class MemberUpdatedForBotsHandler
{
    public static async Task Handle(MemberUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry, IMessageBus bus)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, evt.GuildId);
        if (botUserIds.Count == 0) return;

        var memberResponse = await bus.InvokeAsync<GetGuildMemberResponse>(new GetGuildMemberRequest { GuildId = evt.GuildId, UserId = evt.UserId });
        if (memberResponse.Member is null) return;

        var payload = new GuildMemberUpdatePayload
        {
            GuildId = evt.GuildId,
            User = new DiscordUserPayload { Id = evt.UserId, Username = evt.UserId, Bot = memberResponse.Member.IsBot },
            Nick = memberResponse.Member.Nickname,
            Roles = memberResponse.Member.RoleIds,
            JoinedAt = memberResponse.Member.JoinedAt,
            CommunicationDisabledUntil = memberResponse.Member.MutedUntil,
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, "GUILD_MEMBER_UPDATE", payload);
        }
    }
}
