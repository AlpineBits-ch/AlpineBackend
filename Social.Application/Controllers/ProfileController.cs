using System.Security.Claims;
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
using Wolverine;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles")]
public class ProfileController(MicroserviceContext ctx, ILogger<ProfileController> logger, IMessageBus bus) : ControllerBase
{
    // Statuses a client may explicitly choose. Offline is never client-settable — it's
    // purely a function of connection state (see UserActiveHandler/UserInactiveHandler);
    // "invisible" is expressed by choosing Hidden while still connected.
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
        
        var profile = await ctx.Profiles.Include(profile => profile.Relationships).FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null)
        {
            logger.LogInformation("profile not found for id {id}", id);
            return NotFound();
        }

        return Ok(profile.ToFacet<Profile, ProfileDto>());

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
        
        var profile = await ctx.Profiles.Include(profile => profile.Relationships).FirstOrDefaultAsync(p => p.UserId == id);
        if (profile is null)
        {
            logger.LogInformation("profile not found for user id {id}", id);
            return NotFound();
        }

        return Ok(profile.ToFacet<Profile, ProfileDto>());
    }
}