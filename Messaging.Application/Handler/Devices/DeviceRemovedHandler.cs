using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Handler.Devices;

/// <summary>Gets a removed device out of the MLS groups it holds a leaf in.</summary>
public class DeviceRemovedHandler
{
    public async Task Handle(
        DeviceRemoved message,
        MicroserviceContext ctx,
        IHubContext<EchoRealtimeHub> hub,
        ILogger<DeviceRemovedHandler> logger)
    {
        var deviceId = message.ClientDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        // Without an owner there is no way to tell this device's artifacts from a namesake's, and
        // guessing would destroy somebody else's. Refusing to act is the only safe reading.
        var userId = message.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning(
                "DeviceRemoved for {DeviceId} carried no UserId; skipping - ClientDeviceId is only "
                + "unique per user and an unscoped sweep would consume another account's Welcomes",
                deviceId);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var pending = await ctx.MlsJoinRequests
            .Where(r => r.RequesterUserId == userId
                        && r.RequesterDeviceId == deviceId
                        && r.State == MlsJoinRequestState.Pending)
            .ToListAsync();

        foreach (var request in pending)
        {
            request.State = MlsJoinRequestState.Cancelled;
            request.UpdatedAt = now;
        }

        var welcomes = await ctx.PendingWelcomes
            .Where(w => w.UserId == userId && w.DeviceId == deviceId && w.ConsumedAt == null)
            .ToListAsync();

        foreach (var welcome in welcomes) welcome.ConsumedAt = now;

        var contexts = welcomes.Select(w => w.ContextId)
            .Concat(await ctx.PendingWelcomes
                .Where(w => w.UserId == userId && w.DeviceId == deviceId)
                .Select(w => w.ContextId)
                .ToListAsync())
            .Concat(await ctx.MlsCommits
                .Where(c => c.SenderUserId == userId && c.SenderDeviceId == deviceId)
                .Select(c => c.ContextId)
                .ToListAsync())
            .Distinct()
            .ToList();

        await ctx.SaveChangesAsync();

        if (contexts.Count == 0)
        {
            logger.LogInformation(
                "Device {DeviceId} of user {UserId} was removed and held no MLS artifacts",
                deviceId, message.UserId);
            return;
        }

        // Only contexts that are actually encrypted right now can be re-keyed; a terminated
        // generation has no group left to remove anyone from.
        var live = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .Where(g => contexts.Contains(g.ContextId) && g.State == MlsGenerationState.Active)
            .Select(g => new { g.ContextId, g.ConversationId, g.ChannelId, g.Generation, g.Epoch })
            .ToListAsync();

        foreach (var generation in live)
        {
            var audience = generation.ConversationId is null
                ? []
                : await ctx.Members
                    .Where(m => m.ConversationId == generation.ConversationId)
                    .Select(m => m.UserId)
                    .ToListAsync();

            if (audience.Count == 0) continue;

            await hub.Clients.Users(audience).SendAsync("conversation.MlsDeviceRemoved", new
            {
                contextId = generation.ContextId,
                conversationId = generation.ConversationId,
                channelId = generation.ChannelId,
                generation = generation.Generation,
                epoch = generation.Epoch,
                userId = message.UserId,
                deviceId,
            });
        }

        logger.LogInformation(
            "Device {DeviceId} of user {UserId} removed; nudged {Count} encrypted context(s) to evict its leaf",
            deviceId, message.UserId, live.Count);
    }
}
