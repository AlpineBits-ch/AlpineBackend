using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>
/// Delivers a household notification to specific members, on whatever they have open and on their
/// phone if they have nothing open.
/// </summary>
public class HouseholdNotifier(
    MicroserviceContext ctx,
    NotificationResolutionService notifications,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus)
{
    /// <summary>
    /// Sends to <paramref name="userIds"/>: a realtime event immediately, and a push for those
    /// entitled to one.
    /// </summary>
    public async Task<List<string>> NotifyAsync(
        string guildId, string? channelId, IReadOnlyCollection<string> userIds,
        string eventName, object payload,
        string kind, string title, string body, string? targetId = null)
    {
        var recipients = userIds.Distinct(StringComparer.Ordinal).ToList();
        if (recipients.Count == 0) return [];

        await hub.Clients.Users(recipients).SendAsync(eventName, payload);

        var eligible = await FilterToPushableAsync(guildId, channelId, recipients);
        if (eligible.Count == 0) return [];

        await bus.PublishAsync(new HouseholdPushRequested
        {
            GuildId = guildId,
            ChannelId = channelId,
            UserIds = eligible,
            Kind = kind,
            TargetId = targetId,
            Title = title,
            Body = body,
        });

        return eligible;
    }

    /// <summary>
    /// Drops anyone who has muted this guild or channel, or turned mobile push off.
    /// </summary>
    private async Task<List<string>> FilterToPushableAsync(
        string guildId, string? channelId, IReadOnlyCollection<string> userIds)
    {
        var members = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && userIds.Contains(m.UserId))
            .Select(m => new { m.Id, m.UserId })
            .ToListAsync();

        if (members.Count == 0) return [];

        var memberIds = members.Select(m => m.Id).ToList();

        // Without a channel there is nothing to resolve overrides against, so the guild-level
        // settings are the whole chain - which is what ResolveForChannelAsync degrades to when the
        // channel lookup misses.
        var resolved = await notifications.ResolveForChannelAsync(channelId ?? "", memberIds);

        return members
            .Where(m => resolved.TryGetValue(m.Id, out var setting) && !setting.IsMuted && setting.MobilePush)
            .Select(m => m.UserId)
            .ToList();
    }
}
