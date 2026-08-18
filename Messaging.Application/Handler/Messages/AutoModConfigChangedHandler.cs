using Guild.Contracts.Bus.Events;
using Messaging.Application.Services;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Messages;

/// <summary>Drops Messaging's cached copy of a guild's auto-moderation rules when the guild changes
/// them.</summary>
[NonTransactional]
public class AutoModConfigChangedHandler
{
    public static async Task Handle(AutoModConfigChanged message, IDistributedCache cache)
    {
        foreach (var channelId in message.ChannelIds)
            await AutoModeration.EvictConfigAsync(channelId, cache);
    }
}
