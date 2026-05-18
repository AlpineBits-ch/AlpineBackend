using System.Security.Claims;
using System.Text.Json;
using Guild.Application.Hubs;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildVoiceController(
    GuildPermissionService permissions,
    IHubContext<GuildHub> hub,
    IDistributedCache cache,
    MicroserviceContext db) : ControllerBase
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

        // Add user to the target channel voice state
        var voiceState = await LoadOrCreateChannelVoiceStateAsync(channelId, guildId, ct);
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

        await SaveChannelVoiceStateAsync(voiceState, ct);
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
        await hub.Clients.Users(onlineUserIds).SendAsync("UserJoinedVoice",
            new { userId = UserId, channelId, guildId }, ct);

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

        var voiceState = await LoadOrCreateChannelVoiceStateAsync(channelId, guildId, ct);
        return Ok(ChannelVoiceStateResponse.From(voiceState));
    }

    internal async Task LeaveChannelAsync(string channelId, string userId, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(channelId), ct);
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var participant = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is null) return;

        voiceState.Participants.Remove(participant);
        await SaveChannelVoiceStateAsync(voiceState, ct);
        await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId), ct);
        await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId), ct);

        var onlineUserIds = await GetOnlineGuildMemberIdsAsync(voiceState.GuildId);
        await hub.Clients.Users(onlineUserIds).SendAsync("UserLeftVoice",
            new { userId, channelId, guildId = voiceState.GuildId }, ct);
    }

    private async Task<ChannelVoiceState> LoadOrCreateChannelVoiceStateAsync(
        string channelId, string guildId, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(channelId), ct);
        if (raw is not null)
            return JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        return new ChannelVoiceState { ChannelId = channelId, GuildId = guildId };
    }

    private async Task SaveChannelVoiceStateAsync(ChannelVoiceState voiceState, CancellationToken ct)
    {
        await cache.SetStringAsync(
            ChannelVoiceState.GetCacheKey(voiceState.ChannelId),
            JsonSerializer.Serialize(voiceState),
            CacheOptions, ct);
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
