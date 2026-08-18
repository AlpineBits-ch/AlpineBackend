using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Account;

/// <summary>Messaging's participant in the AccountDeletionSaga fan-out.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command, MicroserviceContext ctx)
    {
        var memberships = await ctx.Members
            .Where(m => m.UserId == command.UserId)
            .Include(m => m.Devices)
            .ToListAsync();

        foreach (var membership in memberships)
            ctx.MemberDevices.RemoveRange(membership.Devices);

        ctx.Members.RemoveRange(memberships);

        // Unsent text is still this person's writing, and it is keyed by channel as well as by
        // conversation, so it does not fall out with the memberships above.
        var drafts = await ctx.MessageDrafts.Where(d => d.UserId == command.UserId).ToListAsync();
        ctx.MessageDrafts.RemoveRange(drafts);

        await PurgeMlsArtifactsAsync(ctx, command.UserId, memberships.Select(m => m.ConversationId).ToList());

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "messaging",
        };
    }

    /// <summary>
    /// The MLS rows that belong to the purged person rather than to the groups they were in.
    /// </summary>
    private static async Task PurgeMlsArtifactsAsync(
        MicroserviceContext ctx, string userId, IReadOnlyCollection<string> conversationIds)
    {
        var welcomes = await ctx.PendingWelcomes.Where(w => w.UserId == userId).ToListAsync();
        ctx.PendingWelcomes.RemoveRange(welcomes);

        var requests = await ctx.MlsJoinRequests
            .Where(r => r.RequesterUserId == userId)
            .Include(r => r.Approvals)
            .ToListAsync();

        foreach (var request in requests) ctx.MlsJoinRequestApprovals.RemoveRange(request.Approvals);
        ctx.MlsJoinRequests.RemoveRange(requests);

        var approvals = await ctx.MlsJoinRequestApprovals
            .Where(a => a.ApproverUserId == userId)
            .ToListAsync();
        ctx.MlsJoinRequestApprovals.RemoveRange(approvals);

        if (conversationIds.Count == 0) return;

        // A conversation the purge just emptied has no group left to preserve.
        var stillOccupied = await ctx.Members
            .Where(m => conversationIds.Contains(m.ConversationId) && m.UserId != userId)
            .Select(m => m.ConversationId)
            .Distinct()
            .ToListAsync();

        var orphaned = conversationIds.Except(stillOccupied).ToList();
        if (orphaned.Count == 0) return;

        var commits = await ctx.MlsCommits.Where(c => orphaned.Contains(c.ContextId)).ToListAsync();
        ctx.MlsCommits.RemoveRange(commits);

        var orphanedWelcomes = await ctx.PendingWelcomes
            .Where(w => orphaned.Contains(w.ContextId))
            .ToListAsync();
        ctx.PendingWelcomes.RemoveRange(orphanedWelcomes);

        var orphanedRequests = await ctx.MlsJoinRequests
            .Where(r => orphaned.Contains(r.ContextId))
            .Include(r => r.Approvals)
            .ToListAsync();
        foreach (var request in orphanedRequests) ctx.MlsJoinRequestApprovals.RemoveRange(request.Approvals);
        ctx.MlsJoinRequests.RemoveRange(orphanedRequests);

        var generations = await ctx.MlsGroupGenerations
            .Where(g => orphaned.Contains(g.ContextId))
            .ToListAsync();
        ctx.MlsGroupGenerations.RemoveRange(generations);
    }
}
