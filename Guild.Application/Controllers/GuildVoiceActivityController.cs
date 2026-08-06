using System.Security.Claims;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Controllers;

/// <summary>
/// "Which of my servers has anyone in voice right now" - the snapshot behind the voice indicator in
/// the server list.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/guilds/voice-activity")]
public class GuildVoiceActivityController(
    GuildVoiceActivityStore activityStore,
    GuildPermissionService permissions,
    MicroserviceContext db) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Voice occupancy across every guild the caller is a member of.</summary>
    [HttpGet]
    public async Task<IActionResult> GetVoiceActivity(CancellationToken ct)
    {
        var guildIds = await db.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == UserId)
            .Select(m => m.GuildId)
            .Distinct()
            .ToListAsync(ct);

        var result = new List<GuildVoiceActivityDto>();

        foreach (var guildId in guildIds)
        {
            var activity = await activityStore.LoadAsync(guildId, ct);
            if (activity is null || activity.Channels.Count == 0) continue;

            var channels = new List<GuildVoiceActivityChannelDto>();
            foreach (var (channelId, channel) in activity.Channels)
            {
                if (channel.UserIds.Count == 0) continue;

                // A voice channel can be private, and a count is still a disclosure: it says people
                // are in a room the caller is not allowed to know exists.
                if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.ViewChannel))
                    continue;

                channels.Add(new GuildVoiceActivityChannelDto(
                    channelId,
                    channel.UserIds.Count,
                    channel.UserIds,
                    channel.StreamerIds.Count > 0,
                    channel.StreamerIds));
            }

            if (channels.Count == 0) continue;

            result.Add(new GuildVoiceActivityDto(
                guildId,
                channels.Sum(c => c.ParticipantCount),
                channels.Any(c => c.HasStream),
                channels));
        }

        return Ok(result);
    }
}
