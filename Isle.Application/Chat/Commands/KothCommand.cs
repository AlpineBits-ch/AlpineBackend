using Isle.Api.Services.KingOfTheHill;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

/// <summary>Reports what King of the Hill is doing right now. Mirrors <c>!quests</c>' shape.</summary>
public class KothCommand(
    MicroserviceContext db,
    KingOfTheHillMatchStateStore stateStore,
    KingOfTheHillControlLedger ledger) : ChatCommand
{
    private const string DefinitionDisplayName = "King of the Hill";

    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return await IdleStatusAsync();

        var standings = await ledger.GetStandingsAsync(marker.InstanceId);
        var holder = await ledger.GetHolderStreakAsync(marker.InstanceId);

        var contesting = standings.Count;
        var holderName = holder is { } h ? await ResolveNameAsync(h.SteamId) : null;

        var holderLine = holder is { } holding
            ? $"{holderName ?? holding.SteamId} has held it for {(int)holding.Streak.TotalSeconds}s"
            : "the hill is contested";

        var participation = contesting switch
        {
            0 => "nobody has registered a control tick yet",
            1 => "1 player has registered control ticks",
            _ => $"{contesting} players have registered control ticks",
        };

        return $"King of the Hill is live! {participation} - {holderLine}.";
    }

    private async Task<string> IdleStatusAsync()
    {
        var definition = await db.GameModeDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DisplayName == DefinitionDisplayName);

        if (definition?.LastRunAt is { } lastRun)
        {
            var remaining = definition.Cooldown - (DateTime.UtcNow - lastRun);
            if (remaining > TimeSpan.Zero)
                return $"No King of the Hill match right now. Next available in {Math.Ceiling(remaining.TotalMinutes):F0}m.";
        }

        return "No King of the Hill match right now. It starts once players enter the zone.";
    }

    private async Task<string?> ResolveNameAsync(string steamId)
    {
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.SteamId == steamId);
        return player?.InGameName;
    }

    public override string Name { get; } = "koth";
    public override string Description { get; } = "Shows the current King of the Hill status";
    public override bool IsAdminOnly { get; set; } = false;
    public override TimeSpan Cooldown => TimeSpan.FromSeconds(15);
}
