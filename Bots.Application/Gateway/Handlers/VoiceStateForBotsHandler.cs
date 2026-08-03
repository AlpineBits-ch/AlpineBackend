using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class VoiceStateForBotsHandler
{
    public static async Task Handle(VoiceStateForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility)
    {
        // A null ChannelId is a disconnect, which is genuinely guild-scoped and carries no channel
        // detail - the helper passes those through unfiltered.
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsForChannelAsync(ctx, visibility, evt.GuildId, evt.ChannelId);
        if (botUserIds.Count == 0) return;

        var payload = new VoiceStatePayload
        {
            GuildId = evt.GuildId,
            ChannelId = evt.ChannelId,
            UserId = evt.UserId,
            SessionId = $"{evt.UserId}-{evt.ChannelId ?? "none"}",
            Deaf = evt.Deaf,
            Mute = evt.Mute,
            SelfDeaf = evt.SelfDeaf,
            SelfMute = evt.SelfMute,
            SelfStream = evt.SelfStream,
            SelfVideo = evt.SelfVideo,
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, "VOICE_STATE_UPDATE", payload);
        }
    }
}
