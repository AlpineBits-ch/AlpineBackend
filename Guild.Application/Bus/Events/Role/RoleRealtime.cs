using Echo.Realtime;
using Guild.Application.Services;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Role;

/// <summary>Resolves the audience for a role-lifecycle broadcast and sends it.</summary>
internal static class RoleRealtime
{
    public static async Task BroadcastAsync(IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate,
        string guildId, string method, object payload)
    {
        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        await hub.Clients.Users(presence.Select(p => p.UserId).Distinct().ToList()).SendAsync(method, payload);
    }
}
