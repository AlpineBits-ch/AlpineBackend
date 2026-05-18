using System.Security.Claims;
using System.Text.Json;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ApplicationUserDto = Identity.Application.Dtos.Response.ApplicationUserDto;

namespace Identity.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/users")]
public class UserController(MicroserviceContext ctx) : ControllerBase
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

    [HttpPost("self/device-token")]
    public async Task<IActionResult> CreateDeviceTokenAsync(CreateDeviceTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();

        var user = await ctx.Users.Include(u => u.DeviceTokens).FirstOrDefaultAsync(u => u.Id == userId);
        if(user is null) return NotFound();

        if (user.DeviceTokens.Any(t => t.Token == dto.Token))
        {
            return Accepted();
        }
        
        user.DeviceTokens.Add(new UserDeviceToken
        {
            Id = UserDeviceToken.GenerateId(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Token = dto.Token,
            UserId = userId
        });
        await ctx.SaveChangesAsync();
        return Created();

    }
}