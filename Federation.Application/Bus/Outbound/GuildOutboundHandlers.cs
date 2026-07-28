using Federation.Application.Providers;
using Federation.Application.Services;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Subscribes to the same *ForBots membership events Bots.Application already consumes (Guild.
/// </summary>
public class GuildOutboundHandlers
{
    public static async Task Handle(MemberJoinedForBots message, IFederationProvider provider, UserService userService, MicroserviceContext db, CancellationToken ct)
    {
        var instances = await FederatedResourceLookup.GetActiveInstancesAsync(db, FederatedResourceType.Guild, message.GuildId, ct);
        var senderId = userService.GetFederatedUserId(message.UserId);
        foreach (var instance in instances)
            await provider.JoinChannelAsync($"{message.GuildId}:{instance.Host}", senderId, ct);
    }

    public static async Task Handle(MemberRemovedForBots message, IFederationProvider provider, UserService userService, MicroserviceContext db, CancellationToken ct)
    {
        var instances = await FederatedResourceLookup.GetActiveInstancesAsync(db, FederatedResourceType.Guild, message.GuildId, ct);
        // For a ban, the "acting" user is really whichever admin banned them, but
        // MemberRemovedForBots doesn't carry that id - the removed member's own id is used as a
        // reasonable approximation until that's threaded through.
        var senderId = userService.GetFederatedUserId(message.UserId);
        foreach (var instance in instances)
        {
            var federatedGuildId = $"{message.GuildId}:{instance.Host}";
            if (message.Reason == "Banned")
                await provider.BanGuildMemberAsync(federatedGuildId, message.UserId, senderId, ct);
            else
                // The venta/v0.1 catalogue has no separate "kicked" event, only left/banned -
                // a kick is modeled as a leave from the remote instance's perspective.
                await provider.LeaveChannelAsync(federatedGuildId, senderId, ct);
        }
    }
}
