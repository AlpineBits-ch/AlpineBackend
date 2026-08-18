using Echo.Realtime;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>What one pass of the stale-turn sweep did.</summary>
/// <param name="Nudged">Turns chased, escalations included.</param>
/// <param name="Escalated">Turns that had already been chased once, so the GM was told.</param>
/// <param name="Skipped">Turns handed on because the player had declared an absence.</param>
public sealed record SceneNudgeOutcome(int Nudged, int Escalated, int Skipped);

/// <summary>
/// Chases scenes whose turn has gone stale. Async games stalling silently is the most common way a
/// play-by-post game dies, so this is the feature rather than a nicety.
/// </summary>
public class SceneNudgeService(
    MicroserviceContext ctx,
    SceneService scenes,
    GuildPermissionService permissions,
    IHubContext<EchoRealtimeHub> hub,
    ILogger<SceneNudgeService> logger)
{
    /// <summary>How long a chased turn gets before it is chased again. Play-by-post is measured in
    /// days, so anything shorter reads as nagging rather than as a reminder.</summary>
    private static readonly TimeSpan NudgeGrace = TimeSpan.FromHours(24);

    /// <summary>Bounded so one pass after downtime drains over the following ones rather than
    /// turning into a write burst.</summary>
    private const int BatchSize = 200;

    /// <summary>
    /// Nudges every overdue turn, skipping the ones whose player is away and escalating the ones
    /// already chased once.
    /// </summary>
    /// <param name="ct">Cancels the sweep.</param>
    /// <returns>What the pass did, per outcome.</returns>
    public async Task<SceneNudgeOutcome> SendDueNudgesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var chaseBefore = now - NudgeGrace;

        var due = await ctx.Set<SceneState>()
            .Where(s => s.Status == SceneStatus.Active
                        && s.CurrentTurnPersonaId != null
                        && s.TurnDeadlineAt != null
                        && s.TurnDeadlineAt < now
                        && (s.LastNudgedAt == null || s.LastNudgedAt < chaseBefore))
            .OrderBy(s => s.TurnDeadlineAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return new SceneNudgeOutcome(0, 0, 0);

        var nudged = 0;
        var escalated = 0;
        var skipped = 0;

        foreach (var scene in due)
        {
            var current = scene.CurrentTurnPersonaId!;

            var unavailable = await scenes.UnavailablePersonasAsync(scene.GuildId, [current], now);
            if (unavailable.Contains(current))
            {
                // "Skip my turn until Friday" is what MemberAbsence was built to say, so the turn
                // moves rather than the player being chased on holiday. A scene whose deadline was
                // set by hand rather than by TurnLengthHours comes back off the clock here, which is
                // the same thing the GM's next PATCH would do.
                await scenes.AdvanceAsync(scene, now);
                skipped++;
                continue;
            }

            var owners = await scenes.OwnersByPersonaAsync(scene.GuildId, [current]);
            var recipients = owners.TryGetValue(current, out var players)
                ? new HashSet<string>(players, StringComparer.Ordinal)
                : [];

            var isEscalation = scene.RecordNudge(now);
            if (isEscalation)
            {
                recipients.UnionWith(await ManageScenesHoldersAsync(scene.GuildId));
                escalated++;
            }

            nudged++;

            if (recipients.Count == 0) continue;

            await hub.Clients.Users(recipients.ToList()).SendAsync(SceneService.NudgeEvent, new
            {
                GuildId = scene.GuildId,
                ChannelId = scene.ChannelId,
                PersonaId = current,
                TurnDeadlineAt = scene.TurnDeadlineAt,
                NudgeCount = scene.NudgeCount,
                Escalated = isEscalation,
            }, ct);
        }

        // Nothing commits for a sweep - unlike the endpoints and handlers, which the Wolverine EF
        // middleware wraps.
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation(
            "Scene turn sweep nudged {Nudged}, escalated {Escalated}, skipped {Skipped}",
            nudged, escalated, skipped);

        return new SceneNudgeOutcome(nudged, escalated, skipped);
    }

    /// <summary>
    /// Who to escalate to: the guild owner, plus whoever holds ManageScenes through a role. A
    /// member-level allow overwrite is deliberately not searched for, because finding one means
    /// reading every member row of the guild on a timer to serve a case nothing sets in bulk.
    /// </summary>
    private async Task<List<string>> ManageScenesHoldersAsync(string guildId)
    {
        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        // The mask is a [Flags] ulong stored as numeric, so the bit test happens here rather than in
        // SQL; a guild has tens of roles, not thousands.
        var roles = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .Select(r => new { r.Id, r.ModulePermissions })
            .ToListAsync();

        var roleIds = roles
            .Where(r => (r.ModulePermissions & ModulePermissions.ManageScenes) == ModulePermissions.ManageScenes)
            .Select(r => r.Id)
            .ToList();

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ownerId)) candidates.Add(ownerId);

        if (roleIds.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var fromRoles = await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => roleIds.Contains(rm.RoleId) && (rm.ExpiresAt == null || rm.ExpiresAt > now))
                .Join(ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId),
                    rm => rm.MemberId, m => m.Id, (_, m) => m.UserId)
                .Distinct()
                .ToListAsync();

            candidates.UnionWith(fromRoles);
        }

        var holders = new List<string>(candidates.Count);
        foreach (var userId in candidates)
        {
            // Confirmed rather than inferred: the role grant says nothing about a member-level deny
            // or about the module being switched off since.
            if (await permissions.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageScenes))
                holders.Add(userId);
        }

        return holders;
    }
}
