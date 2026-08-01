using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Account;

/// <summary>
/// Messaging's participant in the AccountDeletionSaga fan-out. Deliberately does NOT touch
/// Message.AuthorId, Reaction.UserId, or Call/CallParticipant rows - none of those denormalize
/// author display data (Message.cs has no cached username/avatar field), so they keep resolving
/// live to the now-tombstoned Identity/Social rows and "Deleted User" shows up automatically,
/// the same mechanism Discord uses. Message/reaction content itself is neither owned solely by
/// the deleted user nor deletable without corrupting other participants' conversation history.
///
/// ConversationMember (and its per-device rows) IS removed, since that's active membership, not
/// historical content - same reasoning as Guild.GuildMember. This is what makes the deleted
/// user actually disappear from a DM/group's member list going forward.
/// </summary>
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

        await PurgeMlsArtifactsAsync(ctx, command.UserId, memberships.Select(m => m.ConversationId).ToList());

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "messaging",
        };
    }

    /// <summary>
    /// The MLS rows that belong to the purged person rather than to the groups they were in.
    ///
    /// <para>These were all being left behind: a purged account's Welcomes, its outstanding join
    /// requests - which carry a <b>key package and a signature-key fingerprint</b>, the two most
    /// identifying artifacts in the schema - and its approvals of other people's requests.</para>
    ///
    /// <para><b>Commits and generations are deliberately not deleted by author.</b> A commit is not
    /// the sender's property; it is a link in the group's history, and removing one forks every
    /// other member permanently off a group they are still using. A generation is the only record of
    /// which group can read a stretch of ciphertext still sitting in the conversation. What is
    /// removed is the rows for contexts <i>nobody is left in</i> - a conversation whose last member
    /// was this account - where there is no history left to fork and nobody to strand. The
    /// SenderUserId on a surviving commit is not scrubbed either: blanking it would make the
    /// generation look like it had one fewer actor, and the admission threshold is computed from
    /// exactly that count.</para>
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

        // A conversation the purge just emptied has no group left to preserve. Anything else keeps
        // its commits and generations, because other people are still reading them.
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
