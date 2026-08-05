using Isle.Api.Services.KingOfTheHill;

namespace Isle.Api.Chat.Commands;

/// <summary>Manual control over King of the Hill. Mirrors <c>!questadmin</c>'s verb style.</summary>
public class KothAdminCommand(
    KingOfTheHillDirector director,
    KingOfTheHillSpawner spawner,
    KingOfTheHillCompletionService completion,
    KingOfTheHillMatchStateStore stateStore,
    KingOfTheHillControlLedger ledger) : ChatCommand
{
    private const string Usage = "Usage: !kothadmin start | end | status";

    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var args = context.Arguments.ToArray();
        var verb = args.FirstOrDefault()?.ToLowerInvariant();

        return verb switch
        {
            "start" => await StartAsync(),
            "end" => await EndAsync(),
            "status" => await StatusAsync(),
            _ => Usage,
        };
    }

    /// <summary>!kothadmin start - bypasses the trigger gate, same as !questadmin spawn. Cooldown still applies.</summary>
    private async Task<string> StartAsync()
    {
        if (await stateStore.ReadAsync() is not null)
            return "A King of the Hill match is already running.";

        var definition = await director.ChooseForcedAsync();
        if (definition is null)
            return "King of the Hill is not enabled, or is still on cooldown.";

        var instance = await spawner.SpawnAsync(definition);
        return $"Started King of the Hill (instance {instance.InstanceId}).";
    }

    /// <summary>!kothadmin end - cancels the running match, no payout, no history row.</summary>
    private async Task<string> EndAsync()
    {
        var cancelled = await completion.CancelAsync();
        return cancelled ? "King of the Hill match cancelled." : "No King of the Hill match is running.";
    }

    private async Task<string> StatusAsync()
    {
        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return "No King of the Hill match is running.";

        var standings = await ledger.GetStandingsAsync(marker.InstanceId);
        if (standings.Count == 0)
            return $"Instance {marker.InstanceId}: no control ticks credited yet.";

        var lines = standings.Take(10).Select(s => $"{s.SteamId}={s.Ticks}");
        return $"Instance {marker.InstanceId}: {string.Join(", ", lines)}";
    }

    public override string Name { get; } = "kothadmin";
    public override string Description { get; } = "King of the Hill admin. " + Usage;
    public override bool IsAdminOnly { get; set; } = true;
}
