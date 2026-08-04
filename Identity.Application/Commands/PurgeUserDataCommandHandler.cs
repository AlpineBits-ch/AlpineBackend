using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

/// <summary>Identity's own participant in the AccountDeletionSaga fan-out - the actual
/// anonymize-in-place tombstone step. See ApplicationUser.Tombstone for why this alone is what
/// makes "Deleted User" show up everywhere without touching any other service's rows.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command, MicroserviceContext ctx)
    {
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == command.UserId);
        user?.Tombstone(new TombstoneOptions
        {
            AgeOfMajority = Env.Privacy.AgeOfMajority,
            RetainWasVerifiedAdult = Env.Privacy.RetainWasVerifiedAdult,
        });

        // The tombstone anonymizes the row in place rather than deleting it, so the FK cascades
        // never fire - which left a purged account's devices and push tokens alive, still receiving
        // calls and messages on a handset whose account no longer exists.
        var devices = await ctx.UserDevices.Where(d => d.UserId == command.UserId).ToListAsync();
        ctx.UserDevices.RemoveRange(devices);

        var tokens = await ctx.UserPushTokens.Where(t => t.UserId == command.UserId).ToListAsync();
        ctx.UserPushTokens.RemoveRange(tokens);

        // T1-9.
        var sessions = await ctx.LoginSessions
            .Where(s => s.UserId == command.UserId && (s.IpAddress != null || s.UserAgent != null))
            .ToListAsync();

        foreach (var session in sessions)
        {
            session.IpAddress = null;
            session.UserAgent = null;
        }

        var auditEvents = await ctx.IdentityAuditEvents
            .Where(a => a.UserId == command.UserId && a.IpAddress != null)
            .ToListAsync();

        foreach (var auditEvent in auditEvents)
        {
            auditEvent.IpAddress = null;
        }

        // Consent records are personal data of a purged account and, unlike the audit log, they are
        // not evidence of anything once the account is gone: what they prove is that a particular
        // person agreed to particular terms, and there is no longer a person to hold to them.
        var consents = await ctx.UserConsents.Where(c => c.UserId == command.UserId).ToListAsync();
        ctx.UserConsents.RemoveRange(consents);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "identity",
        };
    }
}
