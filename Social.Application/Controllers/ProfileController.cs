using System.Security.Claims;
using System.Text.RegularExpressions;
using Facet.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Social.Contracts.Bus.Integration.Events;
using Social.Api.Services;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles")]
public partial class ProfileController(
    MicroserviceContext ctx,
    ILogger<ProfileController> logger,
    IMessageBus bus,
    ProfileProjectionService projection,
    ActivityWriteGuard activityGuard,
    IDistributedCache cache) : ControllerBase
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();

    // Statuses a client may explicitly choose.
    private static readonly HashSet<OnlineStatus> SettableStatuses =
        [OnlineStatus.Online, OnlineStatus.Idle, OnlineStatus.DoNotDisturb, OnlineStatus.Hidden];

    [Authorize]
    [HttpPatch("me/status")]
    public async Task<IActionResult> SetStatusAsync(SetStatusDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        if (!Enum.TryParse<OnlineStatus>(dto.Status, ignoreCase: true, out var status) || !SettableStatuses.Contains(status))
            return BadRequest($"Status must be one of: {string.Join(", ", SettableStatuses)}");

        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null) return NotFound();

        profile.OnlineStatus = status;
        await ctx.SaveChangesAsync();

        await bus.PublishAsync(new UserStatusChanged { UserId = userId, Status = status.ToString() });

        return Ok(profile.ToFacet<Profile, ProfileDto>());
    }

    /// <summary>How often a client may report activity.</summary>
    private static readonly TimeSpan ActivityWriteInterval = TimeSpan.FromSeconds(15);

    /// <summary>The floor for clearing activity.</summary>
    private static readonly TimeSpan ActivityClearInterval = TimeSpan.FromSeconds(2);

    /// <summary>Replaces the caller's activity list.</summary>
    [Authorize]
    [HttpPut("me/activity")]
    public async Task<IActionResult> SetActivityAsync(SetActivityDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var isClear = dto.Activities is null || dto.Activities.Count == 0;

        // Clears get their own, much shorter window rather than bypassing the limiter: someone who
        // quits a game a second after launching it must not stay visible as playing for fifteen
        // seconds, but an unlimited path is a fan-out amplifier - every clear reaches every guild
        // the user is in.
        var interval = isClear ? ActivityClearInterval : ActivityWriteInterval;
        var throttleKey = isClear ? $"activity:throttle:clear:{userId}" : $"activity:throttle:{userId}";

        if (await cache.GetStringAsync(throttleKey, ct) is not null)
        {
            Response.Headers.RetryAfter = ((int)interval.TotalSeconds).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Read-then-write, so two simultaneous requests can both pass.
        await cache.SetStringAsync(throttleKey, "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = interval }, ct);

        var sanitized = await activityGuard.SanitizeAsync(dto.Activities, DateTimeOffset.UtcNow, ct);

        if (!isClear && sanitized.Count == 0)
        {
            logger.LogDebug("Activity write for {UserId} produced nothing publishable; clearing instead.", userId);
        }

        await bus.PublishAsync(new UserActivityChanged { UserId = userId, Activities = sanitized });

        // Returns what was actually published rather than 204, because the client cannot work it
        // out for itself: an RPC payload carries an application id and no name - Discord's protocol
        // has the name resolved centrally - so the caller genuinely does not know what it just told
        // everyone it was doing.
        return Ok(new SetActivityResultDto { Activities = sanitized });
    }

    // Bio/AccentColor: null leaves the field unchanged, "" clears it.
    [Authorize]
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateProfileAsync(UpdateProfileDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        if (dto.AccentColor is { Length: > 0 } && !HexColorRegex().IsMatch(dto.AccentColor))
            return BadRequest("AccentColor must be a hex color like #5865F2.");

        ProfileFont? font = null;
        if (dto.Font is not null)
        {
            if (!Enum.TryParse<ProfileFont>(dto.Font, ignoreCase: true, out var parsedFont))
                return BadRequest($"Font must be one of: {string.Join(", ", Enum.GetNames<ProfileFont>())}");
            font = parsedFont;
        }

        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null) return NotFound();

        if (dto.Bio is not null) profile.Bio = dto.Bio.Length == 0 ? null : dto.Bio;
        if (dto.AccentColor is not null) profile.AccentColor = dto.AccentColor.Length == 0 ? null : dto.AccentColor;
        if (font is not null) profile.Font = font.Value;

        await ctx.SaveChangesAsync();

        return Ok(profile.ToFacet<Profile, ProfileDto>());
    }


    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> SelfAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            logger.LogInformation("{user}", User.Identity);
            return NotFound();
        }
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null)
        {
            logger.LogInformation("profile not found for user {userId}", userId);
            return NotFound();
        }
        return Ok(profile.ToFacet<Profile, ProfileDto>());
    }
    
    [Authorize]
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            logger.LogInformation("{user}", User.Identity);
            return NotFound();
        }
        
        
        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);

        if (currentProfile is null)
        {
            return NotFound("Current profile not found");
        }
        
        // Deliberately does NOT Include Relationships.
        var profile = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null)
        {
            logger.LogInformation("profile not found for id {id}", id);
            return NotFound();
        }

        // Privacy spec T0-5/T2-17/T2-19: Hidden renders as Offline, the four field-visibility
        // settings are applied, activity is gated on ShareActivity, and a blocked reader gets the
        // minimal public projection - all inside the projection, so a field the viewer may not see
        // is absent from the body rather than present and ignored.
        return Ok(await projection.ProjectAsync(profile, currentProfile.Id));

    }
    
    [Authorize]
    [HttpGet("by-user/{id}")]
    public async Task<IActionResult> GetByUserIdAsync(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            logger.LogInformation("{user}", User.Identity);
            return NotFound();
        }
        
        
        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);

        if (currentProfile is null)
        {
            return NotFound("Current profile not found");
        }
        
        // See GetAsync - no Relationships Include on an arbitrary target's profile.
        var profile = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == id);
        if (profile is null)
        {
            logger.LogInformation("profile not found for user id {id}", id);
            return NotFound();
        }

        // Same gate as GetAsync - the two routes differ only in how the subject is addressed, so
        // neither may be the lenient one.
        return Ok(await projection.ProjectAsync(profile, currentProfile.Id));
    }
}