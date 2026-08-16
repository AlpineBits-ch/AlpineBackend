using System.Security.Claims;
using System.Text.Json;
using Echo.Voice.Rooms;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

/// <summary>Where the server currently places this user in guild voice, if anywhere.</summary>
/// <param name="ChannelName">For the caller's own UI.</param>
public record VoiceStateDto(
    string GuildId,
    string ChannelId,
    string? ChannelName,
    string? DeviceId,
    DateTime JoinedAt);

/// <summary>
/// "Does the server think I am in a voice channel right now?" - the launch read behind the
/// reconnect banner.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/voice/state")]
public class GuildVoiceStateController(
    IDistributedCache cache,
    VoiceRoomStore rooms,
    GuildPermissionService permissions,
    MicroserviceContext db) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>The caller's guild voice seat, or <c>204</c>.</summary>
    [HttpGet]
    public async Task<IActionResult> GetVoiceState(CancellationToken ct)
    {
        var locationJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(UserId), ct);
        if (locationJson is null) return NoContent();

        var location = JsonSerializer.Deserialize<UserVoiceLocation>(locationJson);
        if (location is null || string.IsNullOrWhiteSpace(location.ChannelId)) return NoContent();

        var participant = (await rooms.LoadAsync(VoiceRoomKey.Channel(location.ChannelId), ct))
            ?.Find(UserId);
        if (participant is null) return NoContent();

        if (!await permissions.CanUserPerformActionAsync(UserId, location.ChannelId, Permissions.ViewChannel))
            return NoContent();

        var channelName = await db.Channels
            .AsNoTracking()
            .Where(c => c.Id == location.ChannelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        return Ok(new VoiceStateDto(
            location.GuildId,
            location.ChannelId,
            channelName,
            participant.DeviceId,
            participant.JoinedAt));
    }
}
