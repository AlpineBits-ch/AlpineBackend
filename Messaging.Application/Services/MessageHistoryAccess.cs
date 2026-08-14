using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>
/// The one place that decides whether a caller may read a guild channel's backlog.
/// </summary>
public static class MessageHistoryAccess
{
    public static async Task<bool> MayReadAsync(string channelId, string userId, IMessageBus bus)
    {
        if (!await MayViewAsync(channelId, userId, bus)) return false;

        var history = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = channelId,
                UserId = userId,
                Permission = ExternalPermission.ReadMessageHistory,
            });

        return history.IsAllowed;
    }

    /// <summary>
    /// The first half of <see cref="MayReadAsync"/> on its own: may this caller see that the
    /// channel exists at all.
    /// </summary>
    public static async Task<bool> MayViewAsync(string channelId, string userId, IMessageBus bus)
    {
        var view = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = channelId,
                UserId = userId,
                Permission = ExternalPermission.ViewChannel,
            });

        return view.IsAllowed;
    }

    /// <summary>
    /// <see cref="MayReadAsync"/> for many channels at once, for the batched inbox read.
    /// </summary>
    public static async Task<HashSet<string>> FilterReadableAsync(
        IReadOnlyCollection<string> channelIds, string userId, IMessageBus bus)
    {
        if (channelIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var visible = await bus.InvokeAsync<FilterChannelsWithUserPermissionResponse>(
            new FilterChannelsWithUserPermissionRequest
            {
                UserId = userId,
                ChannelIds = channelIds,
                Permission = ExternalPermission.ViewChannel,
            });

        if (visible.AllowedChannelIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var readable = await bus.InvokeAsync<FilterChannelsWithUserPermissionResponse>(
            new FilterChannelsWithUserPermissionRequest
            {
                UserId = userId,
                ChannelIds = visible.AllowedChannelIds,
                Permission = ExternalPermission.ReadMessageHistory,
            });

        return readable.AllowedChannelIds.ToHashSet(StringComparer.Ordinal);
    }
}
