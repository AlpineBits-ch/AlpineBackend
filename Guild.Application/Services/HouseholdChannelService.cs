using Echo.Realtime;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Shared plumbing for the five household module endpoints (lists, chores, ledger,
/// pantry, decisions), all of which start the same way: resolve a channel, check it's the right
/// module type, check the caller's permission on *that channel*, then broadcast the result.
///
/// Permissions resolve per channel rather than per guild - the same choice ForumTagEndpoint made -
/// so a channel overwrite granting someone control of one list doesn't hand them every list in the
/// house.</summary>
public class HouseholdChannelService(
    MicroserviceContext ctx,
    GuildPermissionService permissionService,
    GuildHydrateService hydrateService,
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

    /// <summary>Broadcasts to everyone currently present in the guild. Household modules are
    /// collaborative in real time - two people in the same shop need to see each other's ticks -
    /// so every mutation goes out this way.</summary>
    public async Task BroadcastAsync(string guildId, string eventName, object payload)
    {
        var presence = await hydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync(eventName, payload);
    }
}
