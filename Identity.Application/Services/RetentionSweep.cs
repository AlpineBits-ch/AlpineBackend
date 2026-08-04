using AppEnvironment;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>What one sweep pass did. Returned rather than only logged so a test can assert on it
/// without parsing log output.</summary>
public sealed record RetentionSweepResult(
    int LoginSessionsScrubbed,
    int AuditEventIpsScrubbed,
    int RevokedLoginSessionsDeleted)
{
    public int Total => LoginSessionsScrubbed + AuditEventIpsScrubbed + RevokedLoginSessionsDeleted;
}

/// <summary>
/// The actual retention work of T1-8, separated from the hosted service that schedules it.
///
/// <para>Split apart on purpose: a <c>BackgroundService</c> can only be tested by starting a host and
/// waiting, which is how retention logic ends up with no tests at all. This is a plain method over a
/// context and a clock, so "a 91-day-old session loses its IP and keeps its row" is an assertion
/// rather than an aspiration.</para>
///
/// <para><b>Everything here is a scrub except the one thing that says delete.</b> Scrubbing an IP
/// must not delete the audit row - the event is the record, the IP is the incidental detail - and a
/// login session that has aged past its IP window is still the row that tells a user which devices
/// have signed in.</para>
///
/// <para><b>Load-and-mutate rather than <c>ExecuteUpdate</c>.</b> A set-based update would be one
/// round trip instead of two, but it is untranslatable on the InMemory provider that the unit tests
/// for this run on, and a retention sweep whose only coverage is "it compiles" is how a wrong
/// comparison operator ships. The batch cap keeps the cost bounded on a database that has never been
/// swept; the remainder is picked up on the next tick.</para>
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
        //
        // Measured from CreatedAt (when the login happened), not LastUsedAt: a session refreshed
        // daily for a year would otherwise keep the IP of a login from a year ago indefinitely,
        // which is exactly the data the window exists to age out.
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
            // Only the IP. Action, Detail, ClientDeviceId and CreatedAt are the record of what
            // happened to the account, and nothing in this sweep may touch them.
            audit.IpAddress = null;
        }

        // ── Revoked LoginSession rows, 180 days, deleted ──
        //
        // From RevokedAt, not CreatedAt: a session revoked yesterday is still the answer to "what did
        // I just cut off", however old the login it belonged to was.
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
