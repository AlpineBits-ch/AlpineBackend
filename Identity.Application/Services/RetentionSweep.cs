using AppEnvironment;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>What one sweep pass did.</summary>
public sealed record RetentionSweepResult(
    int LoginSessionsScrubbed,
    int AuditEventIpsScrubbed,
    int RevokedLoginSessionsDeleted)
{
    public int Total => LoginSessionsScrubbed + AuditEventIpsScrubbed + RevokedLoginSessionsDeleted;
}

/// <summary>
/// The actual retention work of T1-8, separated from the hosted service that schedules it.
/// </summary>
public static class RetentionSweep
{
    public static async Task<RetentionSweepResult> RunAsync(
        MicroserviceContext ctx,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var config = Env.Retention;
        var batch = config.SweepBatchSize;

        // ── LoginSession.IpAddress / UserAgent, 90 days, row kept ──
        var sessionCutoff = now - config.LoginSessionIpAndUserAgent;
        var staleSessions = await ctx.LoginSessions
            .Where(s => s.CreatedAt < sessionCutoff && (s.IpAddress != null || s.UserAgent != null))
            .OrderBy(s => s.CreatedAt)
            .Take(batch)
            .ToListAsync(ct);

        foreach (var session in staleSessions)
        {
            session.IpAddress = null;
            session.UserAgent = null;
        }

        // ── IdentityAuditEvent.IpAddress, 180 days, row kept FOREVER ──
        var auditCutoff = now - config.AuditEventIpAddress;
        var staleAudit = await ctx.IdentityAuditEvents
            .Where(a => a.CreatedAt < auditCutoff && a.IpAddress != null)
            .OrderBy(a => a.CreatedAt)
            .Take(batch)
            .ToListAsync(ct);

        foreach (var audit in staleAudit)
        {
            // Only the IP.
            audit.IpAddress = null;
        }

        // ── Revoked LoginSession rows, 180 days, deleted ──
        var revokedCutoff = now - config.RevokedLoginSession;
        var deadSessions = await ctx.LoginSessions
            .Where(s => s.RevokedAt != null && s.RevokedAt < revokedCutoff)
            .OrderBy(s => s.RevokedAt)
            .Take(batch)
            .ToListAsync(ct);

        ctx.LoginSessions.RemoveRange(deadSessions);

        await ctx.SaveChangesAsync(ct);

        return new RetentionSweepResult(
            // A session can appear in both lists - old enough to be scrubbed and revoked long enough
            // ago to be deleted. Deleting it wins, and it is not double-counted as a scrub.
            staleSessions.Count(s => deadSessions.All(d => d.Id != s.Id)),
            staleAudit.Count,
            deadSessions.Count);
    }
}
