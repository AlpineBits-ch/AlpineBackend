using System.Security.Claims;
using Identity.Application.Dtos.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Controllers;

/// <summary>
/// Lets a user see and revoke their own logins (LoginSession rows created by ConnectController.
/// Exchange). Revoking blocks all future refreshes for that session; the session's current access
/// token remains valid until it naturally expires - see ConnectController's refresh-token branch
/// for the enforcement point.
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

        // The client device id, not the row id - it is the value the client itself knows this
        // machine by (X-Device-Id / the MLS ClientDeviceId), so a client can tell which entry in
        // this list is the machine it is running on without matching on a display name.
        var deviceIds = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .ToDictionaryAsync(d => d.Id, d => d.ClientDeviceId);

        return Ok(sessions.Select(s => new SessionDto
        {
            Id = s.Id,
            DeviceName = s.DeviceName,
            DeviceType = s.DeviceType,
            IpAddress = s.IpAddress,
            CreatedAt = s.CreatedAt,
            LastUsedAt = s.LastUsedAt,
            IsCurrent = s.Id == currentSessionId,
            ClientDeviceId = s.DeviceId is not null && deviceIds.TryGetValue(s.DeviceId, out var clientDeviceId)
                ? clientDeviceId
                : null,
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

            // Revoking the login has to take the push endpoints down with it, or the signed-out
            // handset keeps ringing for calls and buzzing for messages indefinitely - nothing else
            // ever expires a token. Only possible for sessions that recorded which device they
            // came from; older sessions have no link and leave their tokens in place.
            if (session.DeviceId is not null)
            {
                var tokens = await ctx.UserPushTokens
                    .Where(t => t.DeviceId == session.DeviceId)
                    .ToListAsync();
                ctx.UserPushTokens.RemoveRange(tokens);
            }

            await ctx.SaveChangesAsync();
        }

        return NoContent();
    }
}
