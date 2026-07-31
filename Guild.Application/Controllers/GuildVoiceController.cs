using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Realtime.Sfu;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildVoiceController(
    GuildPermissionService permissions,
    IHubContext<EchoRealtimeHub> hub,
    IDistributedCache cache,
    LockedJsonCacheStore voiceStore,
    IDistributedLockService locks,
    CloudflareService cfService,
    MicroserviceContext db,
    DeviceIdResolver devices,
    IMessageBus bus) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>See Messaging.Application.Controllers.VoiceController.ResolveDeviceAsync - one
    /// shared resolver, same fallback for pre-update clients, same rejection of an id this user
    /// has no registered device for.</summary>
    private Task<DeviceIdResult> ResolveDeviceAsync(CancellationToken ct = default) =>
        devices.ResolveAsync(Request, UserId, ct);

    [HttpPost("join")]
    public async Task<IActionResult> Join(string guildId, string channelId, CancellationToken ct)
    {
        var canConnect = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect);
        if (!canConnect) return Forbid();

        var channel = await db.Channels
            .AsNoTracking()
            .Select(c => new { c.Id, c.GuildId, c.Type })
            .FirstOrDefaultAsync(c => c.Id == channelId && c.GuildId == guildId, ct);

        if (channel is null) return NotFound();
        if (channel.Type != Guild.Domain.Enums.ChannelType.Voice) return BadRequest("Channel is not a voice channel");

        var device = await ResolveDeviceAsync(ct);
        if (device.IsUnknown)
            return BadRequest($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");
        var deviceId = device.DeviceId;

        // A user can only be in one voice channel, on one device, at a time, app-wide. If
        // they're already active somewhere else, resolve that first:
        //  - same channel, different device -> takeover (kick the old device, keep the roster
        //    entry, transfer it to the new device)
        //  - anywhere else (any other channel, in this guild or a different one) -> stale
        //    presence, clean leave
        var existingChannelJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(UserId), ct);
        if (existingChannelJson is not null)
        {
            var existing = JsonSerializer.Deserialize<UserVoiceLocation>(existingChannelJson);
            if (existing is not null)
            {
                if (existing.ChannelId == channelId)
                {
                    if (existing.DeviceId is not null && existing.DeviceId != deviceId)
                        await TakeoverDeviceAsync(guildId, channelId, UserId, existing.DeviceId, deviceId, ct);
                }
                else
                {
                    await LeaveChannelAsync(existing.ChannelId, UserId, ct);
                }
            }
        }

        // Add user to the target channel voice state.
        var voiceState = await JoinChannelVoiceStateAsync(channelId, guildId, deviceId, ct);

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId, DeviceId = deviceId }),
            CacheOptions, ct);
        await cache.SetStringAsync(
            ChannelVoiceState.GetHeartbeatCacheKey(UserId),
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90) },
            ct);

        var onlineUserIds = await GetOnlineGuildMemberIdsAsync(guildId);
        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserJoinedVoice",
            new { userId = UserId, channelId, guildId }, ct);

        await bus.PublishAsync(new VoiceStateForBots { GuildId = guildId, UserId = UserId, ChannelId = channelId });

        return Ok(ChannelVoiceStateResponse.From(voiceState));
    }

    /// <summary>Same user, same channel, a different device just joined - transfer the
    /// connection instead of running two. Tells exactly the old device to disconnect, and
    /// best-effort closes its stale Cloudflare session server-side too (the device may be
    /// backgrounded/unreachable and never process the push).</summary>
    private async Task TakeoverDeviceAsync(string guildId, string channelId, string userId, string oldDeviceId, string newDeviceId, CancellationToken ct)
    {
        string? oldCfSessionId = null;
        string? oldAudioTrackName = null;

        await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(channelId), ChannelVoiceState.GetCacheKey(channelId),
            vs =>
            {
                var participant = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (participant is null) return;
                oldCfSessionId = participant.CfSessionId;
                oldAudioTrackName = participant.AudioTrackName;
                participant.DeviceId = newDeviceId;
                participant.CfSessionId = null;
                participant.AudioTrackName = null;
            }, CacheOptions, ct);

        await hub.Clients.Group(EchoRealtimeHub.DeviceGroup(userId, oldDeviceId))
            .SendAsync("guild.voice.KickedByOtherDevice", new { channelId, guildId }, ct);

        if (oldCfSessionId is null || oldAudioTrackName is null) return;
        try
        {
            await cfService.CloseTracksAsync(oldCfSessionId, [oldAudioTrackName], ct);
        }
        catch (CloudflareCallsException)
        {
            // Best-effort - the old device still tears itself down client-side from the kick above.
        }
    }

    [HttpPost("leave")]
    public async Task<IActionResult> Leave(string guildId, string channelId, CancellationToken ct)
    {
        await LeaveChannelAsync(channelId, UserId, ct);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> GetVoiceState(string guildId, string channelId, CancellationToken ct)
    {
        var canView = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.ViewChannel);
        if (!canView) return Forbid();

        var voiceState = await voiceStore.LoadAsync<ChannelVoiceState>(ChannelVoiceState.GetCacheKey(channelId), ct)
            ?? new ChannelVoiceState { ChannelId = channelId, GuildId = guildId };
        return Ok(ChannelVoiceStateResponse.From(voiceState));
    }

    /// <summary>Locked load-or-create-then-add-participant -see the comment on the Join
    /// endpoint above for why this needs the lock (not just the mutate-existing case that
    /// <see cref="LockedJsonCacheStore.UpdateAsync{T}"/> covers, since the very first joiner
    /// to a channel has no existing entry to lock onto via that path).</summary>
    private async Task<ChannelVoiceState> JoinChannelVoiceStateAsync(string channelId, string guildId, string deviceId, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(ChannelVoiceState.GetCacheKey(channelId), ct: ct);

        var voiceState = await voiceStore.LoadAsync<ChannelVoiceState>(ChannelVoiceState.GetCacheKey(channelId), ct)
            ?? new ChannelVoiceState { ChannelId = channelId, GuildId = guildId };

        var existing = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
        if (existing is null)
        {
            voiceState.Participants.Add(new VoiceState
            {
                UserId = UserId,
                ChannelId = channelId,
                GuildId = guildId,
                DeviceId = deviceId,
                JoinedAt = DateTime.UtcNow
            });
        }
        else
        {
            // Same device reconnecting, or the takeover above already updated this - either way,
            // make sure the roster reflects the device that's actually joining now.
            existing.DeviceId = deviceId;
        }

        await voiceStore.SaveAsync(ChannelVoiceState.GetCacheKey(channelId), voiceState, CacheOptions, ct);
        return voiceState;
    }

    internal async Task LeaveChannelAsync(string channelId, string userId, CancellationToken ct)
    {
        ChannelVoiceState? voiceState;
        await using (await locks.AcquireAsync(ChannelVoiceState.GetCacheKey(channelId), ct: ct))
        {
            voiceState = await voiceStore.LoadAsync<ChannelVoiceState>(ChannelVoiceState.GetCacheKey(channelId), ct);
            var participant = voiceState?.Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant is null) return;

            voiceState!.Participants.Remove(participant);
            await voiceStore.SaveAsync(ChannelVoiceState.GetCacheKey(channelId), voiceState, CacheOptions, ct);
        }

        await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId), ct);
        await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId), ct);

        var onlineUserIds = await GetOnlineGuildMemberIdsAsync(voiceState!.GuildId);
        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId, channelId, guildId = voiceState.GuildId }, ct);

        await bus.PublishAsync(new VoiceStateForBots { GuildId = voiceState.GuildId, UserId = userId, ChannelId = null });
    }

    private async Task<List<string>> GetOnlineGuildMemberIdsAsync(string guildId)
    {
        return await db.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();
    }
}

internal record UserVoiceLocation
{
    public string ChannelId { get; init; } = string.Empty;
    public string GuildId { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
}
