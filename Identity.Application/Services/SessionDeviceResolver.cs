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

    /// <summary>What <see cref="TryClaimAsync"/> did.</summary>
    public enum ClaimResult
    {
        /// <summary>The session was already bound to this exact row. Nothing changed.</summary>
        AlreadyBound,

        /// <summary>The session is now this device. Staged on the context, not committed.</summary>
        Claimed,

        /// <summary>Left alone: no usable session, the session belongs to a different device, or the
        /// row is not adoptable.</summary>
        Refused,
    }

    /// <summary><see cref="ClaimResult.Claimed"/> and <see cref="ClaimResult.AlreadyBound"/> both mean
    /// "the caller is this device"; only the audit trail cares which.</summary>
    public static bool IsSelf(ClaimResult result) => result is not ClaimResult.Refused;

    /// <summary>
    /// Lets a still-unbound session become a device row, for the first-launch case where the login
    /// necessarily happened before the device existed - and for the installed base, whose sessions
    /// predate device binding entirely and can otherwise never acquire one without the password.
    /// </summary>
    public async Task<ClaimResult> TryClaimAsync(ClaimsPrincipal principal, string userId, UserDevice device)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return ClaimResult.Refused;

        var session = await ctx.LoginSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);

        if (session is null) return ClaimResult.Refused;

        if (session.DeviceId is not null)
        {
            return string.Equals(session.DeviceId, device.Id, StringComparison.Ordinal)
                ? ClaimResult.AlreadyBound
                : ClaimResult.Refused;
        }

        if (!await IsAdoptableAsync(device)) return ClaimResult.Refused;

        session.DeviceId = device.Id;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return ClaimResult.Claimed;
    }

    /// <summary>Whether a row is one nobody has ever been.</summary>
    public async Task<bool> IsAdoptableAsync(UserDevice device)
    {
        // Created by the request that is asking.
        if (ctx.Entry(device).State == EntityState.Added) return true;

        if (await ctx.LoginSessions.AnyAsync(s => s.DeviceId == device.Id)) return false;
        if (await ctx.UserDeviceBackups.AnyAsync(b => b.DeviceId == device.Id)) return false;

        // Transfers name devices by ClientDeviceId, not by row id.
        return !await ctx.UserBackupTransfers.AnyAsync(
            t => t.UserId == device.UserId
                 && (t.TargetDeviceId == device.ClientDeviceId || t.SourceDeviceId == device.ClientDeviceId));
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
