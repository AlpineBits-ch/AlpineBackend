using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Handler.Devices;

/// <summary>
/// Gets a removed device out of the MLS groups it holds a leaf in.
///
/// <para><b>Removing a device used to remove nothing.</b> <c>DeviceRemoved</c> was published and had
/// no consumer anywhere in the solution, so a handset that had been sold, stolen or signed out kept
/// its leaf in every group it was ever added to - and kept being able to decrypt every message sent
/// afterwards. There was no post-compromise security at all; "remove device" deleted a database row
/// and nothing else.</para>
///
/// <para><b>What this can and cannot do.</b> The server holds no group keys, so it cannot produce
/// the Remove commit - only a member's client can. What it does is everything short of that, in one
/// place, so the removal actually converges:</para>
///
/// <list type="number">
/// <item>Cancels the device's outstanding join requests, so an in-flight admission does not land
/// after the removal and quietly put the leaf back.</item>
/// <item>Consumes its unclaimed Welcomes, which are single-use keys to groups it must no longer
/// enter.</item>
/// <item>Nudges every member of every affected context to propose and commit the removal, naming the
/// device - so the eviction happens on the devices that can actually perform it.</item>
/// </list>
///
/// <para>Contexts are found from the artifacts the device left behind - the Welcomes it was sent and
/// the commits it published. That is an approximation of "groups it holds a leaf in", and knowingly
/// so: <c>ConversationMemberDevice</c> is the table that would answer it exactly, and nothing writes
/// to it. Over-nudging costs a no-op on a client that finds no such leaf; under-nudging would leave
/// a live leaf in place, so the approximation deliberately errs wide.</para>
/// </summary>
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

        var now = DateTimeOffset.UtcNow;

        var pending = await ctx.MlsJoinRequests
            .Where(r => r.RequesterDeviceId == deviceId && r.State == MlsJoinRequestState.Pending)
            .ToListAsync();

        foreach (var request in pending)
        {
            request.State = MlsJoinRequestState.Cancelled;
            request.UpdatedAt = now;
        }

        var welcomes = await ctx.PendingWelcomes
            .Where(w => w.DeviceId == deviceId && w.ConsumedAt == null)
            .ToListAsync();

        foreach (var welcome in welcomes) welcome.ConsumedAt = now;

        var contexts = welcomes.Select(w => w.ContextId)
            .Concat(await ctx.PendingWelcomes
                .Where(w => w.DeviceId == deviceId)
                .Select(w => w.ContextId)
                .ToListAsync())
            .Concat(await ctx.MlsCommits
                .Where(c => c.SenderDeviceId == deviceId)
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
