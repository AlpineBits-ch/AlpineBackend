using System.Security.Claims;
using Facet.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles")]
public class ProfileController(MicroserviceContext ctx, ILogger<ProfileController> logger) : ControllerBase
{
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