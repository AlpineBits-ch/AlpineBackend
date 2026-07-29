using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;

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
    MicroserviceContext db,
    IMessageBus bus) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

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

        // If the user is already in another voice channel in this guild, leave it first
        var existingChannelJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(UserId), ct);
        if (existingChannelJson is not null)
        {
            var existing = JsonSerializer.Deserialize<UserVoiceLocation>(existingChannelJson);
            if (existing is not null && existing.GuildId == guildId && existing.ChannelId != channelId)
                await LeaveChannelAsync(existing.ChannelId, UserId, ct);
        }

        // Add user to the target channel voice state. Locked: two users joining the same
        // channel at once (or a join racing ExchangeParticipantJoined/CloseTracks below) were
        // an unsynchronized read-modify-write on the same ChannelVoiceState blob -whichever
        // save landed last silently discarded the other's change (e.g. one joiner's
        // participant entry never persisted, or a fresh CfSessionId got clobbered back to
        // null, same class of bug as the 1:1 call flow in Messaging.Application).
        var voiceState = await JoinChannelVoiceStateAsync(channelId, guildId, ct);

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId }),
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
    private async Task<ChannelVoiceState> JoinChannelVoiceStateAsync(string channelId, string guildId, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(ChannelVoiceState.GetCacheKey(channelId), ct: ct);

        var voiceState = await voiceStore.LoadAsync<ChannelVoiceState>(ChannelVoiceState.GetCacheKey(channelId), ct)
            ?? new ChannelVoiceState { ChannelId = channelId, GuildId = guildId };

        if (voiceState.Participants.All(p => p.UserId != UserId))
        {
            voiceState.Participants.Add(new VoiceState
            {
                UserId = UserId,
                ChannelId = channelId,
                GuildId = guildId,
                JoinedAt = DateTime.UtcNow
            });
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
}
