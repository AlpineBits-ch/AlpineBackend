using System.Security.Claims;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>Which registered device the caller's session was established from.</summary>
public sealed class SessionDeviceResolver(MicroserviceContext ctx)
{
    public const string SessionIdClaim = "session_id";

    public static string? SessionIdOf(ClaimsPrincipal principal) => principal.FindFirstValue(SessionIdClaim);

    /// <summary>The active device this session was established from, or null when the session is
    /// unbound, revoked, or points at a device that has since been removed.</summary>
    public async Task<UserDevice?> ResolveAsync(ClaimsPrincipal principal, string userId)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var deviceRowId = await ctx.LoginSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .Select(s => s.DeviceId)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(deviceRowId)) return null;

        return await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceRowId
                                      && d.UserId == userId
                                      && d.Status == DeviceStatus.Active);
    }

    /// <summary>True when the caller's session is bound to this exact device row.</summary>
    public async Task<bool> IsCallingDeviceAsync(ClaimsPrincipal principal, string userId, string deviceRowId)
    {
        var device = await ResolveAsync(principal, userId);
        return device is not null && string.Equals(device.Id, deviceRowId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Binds a still-unbound session to a device row, for the first-launch case where the login
    /// necessarily happened before the device existed.
    /// </summary>
    public async Task<bool> TryBindAsync(ClaimsPrincipal principal, string userId, string deviceRowId)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        var session = await ctx.LoginSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);

        if (session is null || session.DeviceId is not null) return false;

        session.DeviceId = deviceRowId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>The outcome of <see cref="BindExistingAsync"/>.</summary>
    public enum BindResult
    {
        /// <summary>Bound. Staged on the context, not committed.</summary>
        Bound,

        /// <summary>Already bound to this same device.</summary>
        AlreadyBound,

        /// <summary>Bound to a different device.</summary>
        BoundElsewhere,

        /// <summary>No usable session behind the token - no <c>session_id</c> claim, or the row is
        /// gone or revoked.</summary>
        NoSession,
    }

    /// <summary>Binds an unbound session to a device row that already exists.</summary>
    public async Task<BindResult> BindExistingAsync(ClaimsPrincipal principal, string userId, string deviceRowId)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return BindResult.NoSession;

        var session = await ctx.LoginSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);

        if (session is null) return BindResult.NoSession;

        if (session.DeviceId is not null)
        {
            return string.Equals(session.DeviceId, deviceRowId, StringComparison.Ordinal)
                ? BindResult.AlreadyBound
                : BindResult.BoundElsewhere;
        }

        session.DeviceId = deviceRowId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return BindResult.Bound;
    }
}
