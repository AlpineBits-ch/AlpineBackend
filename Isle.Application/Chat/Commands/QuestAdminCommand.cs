using System.Globalization;
using Isle.Api.Services.Quests;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

/// <summary>Manual control over the quest giver.</summary>
public class QuestAdminCommand(
    MicroserviceContext db,
    QuestDirector director,
    QuestSpawner spawner,
    BountyService bounties,
    KillStreakTracker streaks,
    PopulationHeatmap heatmap) : ChatCommand
{
    private const string Usage =
        "Usage: !questadmin bounty <player> [minutes] [bonusXp] | spawn <quest> [region] | end <Q-id> | list | streaks | pop";

    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var args = context.Arguments.ToArray();
        var verb = args.FirstOrDefault()?.ToLowerInvariant();

        return verb switch
        {
            "bounty" => await BountyAsync(args),
            "spawn" => await SpawnAsync(args),
            "end" => await EndAsync(args),
            "list" => await ListAsync(),
            "streaks" => await StreaksAsync(),
            "pop" => Population(),
            _ => Usage
        };
    }

    /// <summary>!questadmin bounty &lt;player&gt; [minutes] [bonusXp]</summary>
    private async Task<string> BountyAsync(string[] args)
    {
        if (args.Length < 2)
            return "Usage: !questadmin bounty steamId|friendlyId|name [minutes] [bonusXp]";

        var resolved = await PlayerResolver.ResolveAsync(db, args[1]);
        switch (resolved.Outcome)
        {
            case PlayerResolver.ResolveOutcome.NotFound:
                return $"No player matches '{args[1]}'.";
            case PlayerResolver.ResolveOutcome.Ambiguous:
                return $"'{args[1]}' matches more than one player. Use their friendly id.";
        }

        var target = resolved.Player!;

        var duration = ParseInt(args, 2) is { } minutes and > 0
            ? TimeSpan.FromMinutes(Math.Min(minutes, 180))
            : BountyService.DefaultDuration;

        var bonusXp = ParseInt(args, 3);

        var instance = await bounties.StartAsync(target.Id, duration, bonusXp, adminSpawned: true);

        if (instance is null)
            return $"Could not mark {target.InGameName ?? target.FriendlyId}. They may already be marked, or no bounty template is configured.";

        var payout = bonusXp is > 0 ? $", bonus {bonusXp:N0} XP" : string.Empty;
        return $"Marked {target.InGameName ?? target.FriendlyId} for {duration.TotalMinutes:F0}m{payout}. Instance {instance.FriendlyId}.";
    }

    /// <summary>!questadmin spawn &lt;questIdOrName&gt; [regionIdOrName]</summary>
    private async Task<string> SpawnAsync(string[] args)
    {
        if (args.Length < 2)
            return "Usage: !questadmin spawn questId|questName [regionId|regionName]";

        // Names contain spaces; everything before an optional trailing region token is the quest name.
        var questName = args[1];
        var regionArg = args.Length > 2 ? string.Join(" ", args.Skip(2)) : null;

        var candidate = await director.ChooseForcedAsync(questName, regionArg);
        if (candidate is null)
            return $"No quest matches '{questName}', or it has no location with a mapped region.";

        var instance = await spawner.SpawnAsync(candidate, adminSpawned: true);
        return $"Spawned '{candidate.Quest.Name}' at {candidate.Region.Name}. Instance {instance.FriendlyId}.";
    }

    /// <summary>!questadmin end &lt;Q-id&gt;</summary>
    private async Task<string> EndAsync(string[] args)
    {
        if (args.Length < 2)
            return "Usage: !questadmin end Q-id|instanceId";

        var needle = args[1];
        var seq = QuestInstance.DecodeFriendlyId(needle);

        var instance = seq is not null
            ? await db.QuestInstances.FirstOrDefaultAsync(i => i.FriendlyIdSeq == seq.Value)
            : await db.QuestInstances.FirstOrDefaultAsync(i => i.Id == needle);

        if (instance is null)
            return $"No quest instance {needle}.";

        if (!instance.IsOpen)
            return $"Instance {instance.FriendlyId} is already {instance.State}.";

        // Bounties need the full teardown (unmark, restore skin), not just a state flip.
        if (instance.Type == QuestType.Bounty && instance.TargetPlayerId is { } targetId)
        {
            await bounties.CancelForPlayerAsync(targetId, QuestInstanceState.Cancelled);
            return $"Bounty {instance.FriendlyId} cancelled and target unmarked.";
        }

        instance.TryClose(QuestInstanceState.Cancelled);
        await db.SaveChangesAsync();
        return $"Instance {instance.FriendlyId} cancelled.";
    }

    private async Task<string> ListAsync()
    {
        var open = await db.QuestInstances
            .AsNoTracking()
            .Where(i => i.State == QuestInstanceState.Active)
            .OrderBy(i => i.ExpiresAt)
            .Take(10)
            .ToListAsync();

        if (open.Count == 0)
            return "Nothing is running.";

        var now = DateTimeOffset.UtcNow;
        return string.Join(" | ", open.Select(i =>
            $"{i.FriendlyId} {i.Type} '{i.Title}' @ {i.LocationName ?? "?"} {Math.Max(0, (int)(i.ExpiresAt - now).TotalMinutes)}m"));
    }

    /// <summary>Shows the live streak board, so the spree thresholds can be tuned against real numbers.</summary>
    private async Task<string> StreaksAsync()
    {
        var board = await streaks.GetLeaderboardAsync();
        if (board.Count == 0)
            return "No active kill streaks.";

        var ids = board.Select(s => s.PlayerId).ToList();
        var names = await db.Players
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.InGameName ?? p.FriendlyId);

        var lines = board.Take(5).Select(s => $"{names.GetValueOrDefault(s.PlayerId, s.PlayerId)}: {s.Kills}");

        return $"Streaks (need {BountyService.MinKillsForSpree}+ and a {BountyService.RequiredLeadOverRunnerUp} lead): {string.Join(", ", lines)}";
    }

    /// <summary>Current population distribution, i.e. exactly what the director is scoring against.</summary>
    private string Population()
    {
        var distribution = heatmap.Distribution();
        if (distribution.Count == 0)
            return "No population data yet.";

        var lines = distribution.Take(6).Select(r => $"{r.Name}: {r.Count} ({r.Share:P0})");
        var hotspot = heatmap.Hotspot();

        var suffix = hotspot is null ? "no hotspot" : $"hotspot {hotspot.Name}";
        return $"{string.Join(", ", lines)} [{heatmap.OnlineCount} online, {suffix}]";
    }

    private static int? ParseInt(string[] args, int index) =>
        args.Length > index && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public override string Name { get; } = "questadmin";
    public override string Description { get; } = "Quest and bounty admin. " + Usage;
    public override bool IsAdminOnly { get; set; } = true;
}
