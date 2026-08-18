using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>What one pass of the stale-turn sweep did.</summary>
/// <param name="Nudged">Turns chased, escalations included.</param>
/// <param name="Escalated">Turns that had already been chased once, so the GM was told.</param>
/// <param name="Skipped">Turns handed on because the player had declared an absence.</param>
/// <param name="Deferred">Turns left for a later pass because the guild is in its quiet hours.</param>
public sealed record SceneNudgeOutcome(int Nudged, int Escalated, int Skipped, int Deferred = 0);

/// <summary>
/// Chases scenes whose turn has gone stale. Async games stalling silently is the most common way a
/// play-by-post game dies, so this is the feature rather than a nicety.
/// </summary>
public class SceneNudgeService(
    MicroserviceContext ctx,
    SceneService scenes,
    PersonaCastService cast,
    GuildPermissionService permissions,
    NotificationResolutionService notifications,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus,
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

        var quietHours = await QuietHoursAsync(due, ct);
        var sceneNames = await SceneNamesAsync(due, ct);
        var personaNames = await PersonaNamesAsync(due);

        var nudged = 0;
        var escalated = 0;
        var skipped = 0;
        var deferred = 0;

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

            // Held rather than sent silently: nothing is recorded, so the pass a quarter of an hour
            // after the window closes chases this turn as if the deadline had just passed.
            if (quietHours.TryGetValue(scene.GuildId, out var quiet) && quiet.DeferPast(now) > now)
            {
                deferred++;
                continue;
            }

            var name = personaNames.GetValueOrDefault(scene.GuildId)?.GetValueOrDefault(current);
            var sceneName = sceneNames.GetValueOrDefault(scene.ChannelId, "");

            if (await ChaseAsync(scene, now, sceneName, name, ct)) escalated++;
            nudged++;
        }

        // Nothing commits for a sweep - unlike the endpoints and handlers, which the Wolverine EF
        // middleware wraps.
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation(
            "Scene turn sweep nudged {Nudged}, escalated {Escalated}, skipped {Skipped}, deferred {Deferred}",
            nudged, escalated, skipped, deferred);

        return new SceneNudgeOutcome(nudged, escalated, skipped, deferred);
    }

    /// <summary>
    /// Chases one turn now, for a GM looking at a stalled scene rather than for the sweep. Quiet
    /// hours and the grace period do not apply: somebody asked for this one.
    /// </summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="now">The instant the nudge went out.</param>
    /// <returns>True when there was a turn to chase.</returns>
    public async Task<bool> NudgeNowAsync(SceneState scene, DateTimeOffset now)
    {
        if (scene.Status != SceneStatus.Active || scene.CurrentTurnPersonaId is null) return false;

        var sceneName = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == scene.ChannelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync() ?? "";

        var named = await cast.ResolveAsync(scene.GuildId, [scene.CurrentTurnPersonaId]);

        await ChaseAsync(
            scene, now, sceneName, named.GetValueOrDefault(scene.CurrentTurnPersonaId)?.Name);

        return true;
    }

    /// <summary>Tells whoever can do something about one stale turn, and escalates on the second
    /// miss.</summary>
    private async Task<bool> ChaseAsync(
        SceneState scene, DateTimeOffset now, string sceneName, string? personaName,
        CancellationToken ct = default)
    {
        var current = scene.CurrentTurnPersonaId!;

        var owners = await scenes.OwnersByPersonaAsync(scene.GuildId, [current]);
        var players = owners.TryGetValue(current, out var found) ? found : [];
        var recipients = new HashSet<string>(players, StringComparer.Ordinal);

        var holders = new List<string>();
        var isEscalation = scene.RecordNudge(now);
        if (isEscalation)
        {
            holders = await ManageScenesHoldersAsync(scene.GuildId);
            recipients.UnionWith(holders);
        }

        if (recipients.Count == 0) return isEscalation;

        await hub.Clients.Users(recipients.ToList()).SendAsync(SceneService.NudgeEvent, new
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            // A nudge for a game somebody had forgotten has to name the game, so the scene's title
            // travels with it rather than being fetched from an id.
            SceneName = sceneName,
            PersonaId = current,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            TurnStartedAt = scene.TurnStartedAt,
            TurnNumber = scene.TurnNumber,
            NudgeCount = scene.NudgeCount,
            Escalated = isEscalation,
        }, ct);

        await PushAsync(scene, current, personaName, sceneName, players, escalatedCopy: false);
        await PushAsync(scene, current, personaName, sceneName,
            holders.Except(players, StringComparer.Ordinal).ToList(), escalatedCopy: true);

        return isEscalation;
    }

    /// <summary>
    /// Asks Messaging for the phone notification, having dropped whoever muted the scene or turned
    /// mobile push off.
    /// </summary>
    private async Task PushAsync(
        SceneState scene, string personaId, string? personaName, string sceneName,
        IReadOnlyCollection<string> userIds, bool escalatedCopy)
    {
        if (userIds.Count == 0) return;

        var pushable = await notifications.PushableUserIdsAsync(scene.GuildId, scene.ChannelId, userIds);
        if (pushable.Count == 0) return;

        await bus.PublishAsync(new SceneTurnPushRequested
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            UserIds = pushable,
            PersonaId = personaId,
            // Masked rather than filled in from the account behind the character: a name nobody can
            // resolve is a name nobody may substitute for.
            AuthorDisplayName = personaName ?? "",
            PersonaHidden = personaName is null,
            SceneName = sceneName,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            NudgeCount = scene.NudgeCount,
            Escalated = escalatedCopy,
        });
    }

    private async Task<Dictionary<string, GuildQuietHoursConfig>> QuietHoursAsync(
        IReadOnlyCollection<SceneState> due, CancellationToken ct)
    {
        var guildIds = due.Select(s => s.GuildId).Distinct().ToList();

        return await ctx.GuildQuietHoursConfigs
            .AsNoTracking()
            .Where(c => guildIds.Contains(c.GuildId) && c.Enabled)
            .ToDictionaryAsync(c => c.GuildId, ct);
    }

    private async Task<Dictionary<string, string>> SceneNamesAsync(
        IReadOnlyCollection<SceneState> due, CancellationToken ct)
    {
        var channelIds = due.Select(s => s.ChannelId).ToList();

        return await ctx.Channels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    /// <summary>The characters on the clock, named per guild in one pair of queries each.</summary>
    private async Task<Dictionary<string, Dictionary<string, string>>> PersonaNamesAsync(
        IReadOnlyCollection<SceneState> due)
    {
        var names = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var group in due.GroupBy(s => s.GuildId, StringComparer.Ordinal))
        {
            var personaIds = group
                .Select(s => s.CurrentTurnPersonaId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var resolved = await cast.ResolveAsync(group.Key, personaIds);

            names[group.Key] = resolved.ToDictionary(
                pair => pair.Key, pair => pair.Value.Name, StringComparer.Ordinal);
        }

        return names;
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
