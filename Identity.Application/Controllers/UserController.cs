using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ApplicationUserDto = Identity.Application.Dtos.Response.ApplicationUserDto;

namespace Identity.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/users")]
public class UserController(MicroserviceContext ctx, ILogger<UserController> logger) : ControllerBase
{
    [HttpGet("self")]
    public async Task<IActionResult> GetSelfAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        
        return Ok(ctx.Users.Where(u => u.Id == userId).FirstOrDefault()?
            .ToFacet<ApplicationUser, ApplicationUserDto>());
    }



    [HttpPost("master")]
    public async Task<IActionResult> UploadMasterKey(CreateMasterKeyDto dto)
    
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if(user is null) return NotFound();


        if (user.EncryptedMasterKey?.Version == dto.Version)
        {
            return BadRequest("Master key already uploaded");
        }
   
        user.EncryptedMasterKey = new EncryptedMasterKey()
        {
            Argon2Iterations = dto.Argon2Iterations,
            Salt = dto.Salt,
            Iv = dto.Iv,
            CipherText = dto.CipherText,
            Argon2Memory = dto.Argon2Memory,
            Argon2Parallelism = dto.Argon2Parallelism,
            Version = dto.Version
        };
        await ctx.SaveChangesAsync();
        return Ok();
    }
    
    
    [HttpGet("self/settings")]
    public async Task<IActionResult> GetSettingsAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        
        var user = ctx.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(user.JsonSettings))
        {
            user.JsonSettings = "{}";
            await ctx.SaveChangesAsync();
        }
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(user.JsonSettings);
    
        return Ok(jsonElement);
    }
    
    [HttpPut("self/settings")]
    public async Task<IActionResult> GetSettingsAsync([FromBody] JsonElement body)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        var user = ctx.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            return NotFound();
        }
        string rawJson = body.GetRawText();

        user.JsonSettings = rawJson;
        await ctx.SaveChangesAsync();
        return Ok(user.JsonSettings);
    }

    /// <summary>Registers (or re-points) one push endpoint for the caller.</summary>
    [HttpPost("self/push-token")]
    public async Task<IActionResult> CreatePushTokenAsync(CreatePushTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, dto.Kind, dto.DeviceId);
    }

    /// <summary>Deprecated - POST self/push-token with <c>kind: "Fcm"</c>.</summary>
    [HttpPost("self/device-token")]
    public async Task<IActionResult> CreateDeviceTokenAsync(CreateDeviceTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, PushTokenKind.Fcm, dto.DeviceId);
    }

    /// <summary>Deprecated - POST self/push-token with <c>kind: "ApnsVoip"</c>.</summary>
    [HttpPost("self/voip-token")]
    public async Task<IActionResult> CreateVoipTokenAsync(CreateDeviceTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, PushTokenKind.ApnsVoip, dto.DeviceId);
    }

    /// <summary>
    /// Upsert rather than insert-if-absent: the (kind, token) pair is unique across the table, and
    /// both push providers hand the same token to a different account after a reinstall or an
    /// account switch on the same handset.
    /// </summary>
    private async Task<IActionResult> UpsertPushTokenAsync(string userId, string token, PushTokenKind kind, string? clientDeviceId)
    {
        string? deviceRowId = null;
        if (!string.IsNullOrWhiteSpace(clientDeviceId))
        {
            deviceRowId = await ctx.UserDevices
                .Where(d => d.UserId == userId && d.ClientDeviceId == clientDeviceId)
                .Select(d => d.Id)
                .FirstOrDefaultAsync();

            // An unknown device id is a client bug worth surfacing, but not worth losing the token
            // over - register it unattached rather than dropping the registration.
            if (deviceRowId is null)
            {
                logger.LogWarning("Push token registered by user {UserId} for unknown device {ClientDeviceId}",
                    userId, clientDeviceId);
            }
        }

        var existing = await ctx.UserPushTokens.FirstOrDefaultAsync(t => t.Kind == kind && t.Token == token);
        if (existing is not null)
        {
            existing.ReassignTo(userId, deviceRowId);
            await ctx.SaveChangesAsync();
            return Accepted();
        }

        ctx.UserPushTokens.Add(UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = userId,
            Token = token,
            Kind = kind,
            DeviceId = deviceRowId,
        }));
        await ctx.SaveChangesAsync();
        return Created();
    }

    /// <summary>Lets a client drop its own endpoint on sign-out instead of leaving a token that
    /// keeps ringing a handset nobody is signed in on.</summary>
    [HttpDelete("self/push-token")]
    public async Task<IActionResult> DeletePushTokenAsync([FromQuery] string token, [FromQuery] PushTokenKind? kind)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(token)) return BadRequest("token is required.");

        var rows = await ctx.UserPushTokens
            .Where(t => t.UserId == userId && t.Token == token && (kind == null || t.Kind == kind))
            .ToListAsync();

        if (rows.Count == 0) return NotFound();

        ctx.UserPushTokens.RemoveRange(rows);
        await ctx.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Starts the grace-period countdown rather than deleting anything immediately -
    /// see ApplicationUser.RequestDeletion. Login is blocked from this point on
    /// (IsSigninAllowed), but the request is reversible via self/cancel-deletion until the
    /// purge sweep picks it up.</summary>
    [HttpDelete("self")]
    public async Task<IActionResult> RequestDeletionAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        var purgeScheduledAt = DateTimeOffset.UtcNow.Add(Env.AccountDeletion.GracePeriod);
        user.RequestDeletion(purgeScheduledAt);
        await ctx.SaveChangesAsync();

        return Ok(new { purgeScheduledAt });
    }

    [HttpPost("self/cancel-deletion")]
    public async Task<IActionResult> CancelDeletionAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        if (!user.CancelDeletionRequest())
            return Conflict("Account is not pending deletion, or the purge has already started.");

        await ctx.SaveChangesAsync();
        return Ok();
    }
}