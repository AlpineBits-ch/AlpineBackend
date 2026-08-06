using Echo.Realtime;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Shared plumbing for the five household module endpoints (lists, chores, ledger, pantry,
/// decisions), all of which start the same way: resolve a channel, check it's the right module
/// type, check the caller's permission on that channel, then broadcast the result.
/// </summary>
public class HouseholdChannelService(
    MicroserviceContext ctx,
    GuildPermissionService permissionService,
    GuildHydrateService hydrateService,
    ChannelAudienceService audience,
    IHubContext<EchoRealtimeHub> hub)
{
    public enum Access
    {
        Ok,
        NotFound,
        Forbidden,
    }

    public record ChannelAccess(Access Result, Channel? Channel)
    {
        public IResult? ToFailure() => Result switch
        {
            Access.NotFound => Results.NotFound(),
            Access.Forbidden => Results.Forbid(),
            _ => null,
        };
    }

    /// <summary>Resolves a household channel of the expected type and authorizes the caller
    /// against it. Returns NotFound (rather than Forbidden) for a channel of the wrong type, so
    /// this never doubles as a probe for which channels exist.</summary>
    public async Task<ChannelAccess> ResolveAsync(
        string channelId, ChannelType expectedType, string userId, Permissions required)
    {
        var channel = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null || channel.Type != expectedType) return new ChannelAccess(Access.NotFound, null);

        // The feature gate lives inside CanUserPerformActionAsync, so a guild without the module
        // fails here for everyone including the owner - no extra check needed.
        var allowed = await permissionService.CanUserPerformActionAsync(userId, channelId, required);
        return allowed
            ? new ChannelAccess(Access.Ok, channel)
            : new ChannelAccess(Access.Forbidden, null);
    }

    /// <summary>
    /// Broadcasts a channel-scoped mutation to the online members who can actually see that
    /// channel.
    /// </summary>
    public async Task BroadcastAsync(string guildId, string channelId, string eventName, object payload)
    {
        var presence = await hydrateService.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        var viewers = await audience.FilterToViewersAsync(channelId, presence.Select(p => p.UserId));
        if (viewers.Count == 0) return;

        await hub.Clients.Users(viewers).SendAsync(eventName, payload);
    }

    /// <summary>Broadcasts a guild-scoped event to everyone present.</summary>
    public async Task BroadcastGuildAsync(string guildId, string eventName, object payload)
    {
        var presence = await hydrateService.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync(eventName, payload);
    }
}
