using System.Security.Claims;
using Identity.Application.Dtos.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Controllers;

/// <summary>
/// Lets a user see and revoke their own logins (LoginSession rows created by ConnectController.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/sessions")]
public class SessionController(MicroserviceContext ctx) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSessionsAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var currentSessionId = User.FindFirstValue("session_id");

        var sessions = await ctx.LoginSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastUsedAt)
            .ToListAsync();

        return Ok(sessions.Select(s => new SessionDto
        {
            Id = s.Id,
            DeviceName = s.DeviceName,
            DeviceType = s.DeviceType,
            IpAddress = s.IpAddress,
            CreatedAt = s.CreatedAt,
            LastUsedAt = s.LastUsedAt,
            IsCurrent = s.Id == currentSessionId,
        }));
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> RevokeSessionAsync(string sessionId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var session = await ctx.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null) return NotFound();
        if (session.UserId != userId) return Forbid();

        if (!session.IsRevoked)
        {
            session.Revoke();
            await ctx.SaveChangesAsync();
        }

        return NoContent();
    }
}
