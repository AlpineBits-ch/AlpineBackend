using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>
/// Delivers a household notification to specific members, on whatever they have open and on their
/// phone if they have nothing open.
/// </summary>
public class HouseholdNotifier(
    NotificationResolutionService notifications,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus)
{
    /// <summary>The single realtime event every household alert arrives on.</summary>
    public const string AlertEventName = "guild.HouseholdAlert";

    /// <summary>
    /// Sends one household alert: a realtime event immediately, and a push for those entitled to
    /// one.
    /// </summary>
    public async Task<List<string>> AlertAsync(
        string guildId, string? channelId, IReadOnlyCollection<string> userIds,
        string kind, AlertText title, AlertText body, string? targetId = null, object? data = null)
    {
        var recipients = userIds.Distinct(StringComparer.Ordinal).ToList();
        if (recipients.Count == 0) return [];

        await hub.Clients.Users(recipients).SendAsync(AlertEventName, new
        {
            GuildId = guildId,
            ChannelId = channelId,
            Kind = kind,
            TargetId = targetId,
            Title = title.Text,
            Body = body.Text,
            // Flat rather than nested under a "loc" object: a web client that localizes reads two
            // keys it recognises, and one that does not carries on reading Title and Body without
            // having to know the envelope changed shape.
            TitleLocKey = title.LocKey,
            TitleLocArgs = title.LocArgs,
            BodyLocKey = body.LocKey,
            BodyLocArgs = body.LocArgs,
            Data = data,
        });

        var eligible = await notifications.PushableUserIdsAsync(guildId, channelId, recipients);
        if (eligible.Count == 0) return [];

        await bus.PublishAsync(new HouseholdPushRequested
        {
            GuildId = guildId,
            ChannelId = channelId,
            UserIds = eligible,
            Kind = kind,
            TargetId = targetId,
            Title = title.Text,
            Body = body.Text,
            TitleLocKey = title.LocKey,
            TitleLocArgs = [.. title.LocArgs],
            BodyLocKey = body.LocKey,
            BodyLocArgs = [.. body.LocArgs],
        });

        return eligible;
    }
}
