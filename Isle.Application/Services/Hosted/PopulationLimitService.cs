using Isle.Api.Services.Rcon;
using IsleBridge.Sdk;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Enforces the per-species population caps in <see cref="SpeciesPopulationLimits"/>. Every minute it
/// reads the live roster over RCON, counts how many of each species are alive, and enables species
/// that are under their cap while disabling those that have hit it — so prime dinos (rex, trike, …)
/// can't exceed their configured group limit. Species capped at <see cref="SpeciesPopulationLimits.Unlimited"/>
/// stay permanently enabled.
/// </summary>
public sealed class PopulationLimitService(
    IRconGateway rcon,
    SpeciesPopulationLimits limits,
    ILogger<PopulationLimitService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    
    private Dictionary<string, bool>? _lastState;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Apply once on boot so caps take effect immediately, then reconcile every minute.
        await SafeEnforceAsync();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SafeEnforceAsync();
        }
    }

    private async Task SafeEnforceAsync()
    {
        try
        {
            await EnforceAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Population limit enforcement tick failed");
        }
    }

    private async Task EnforceAsync()
    {
        var players = await rcon.ExecuteAsync(client => client.GetPlayerData());

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in players)
        {
            if (string.IsNullOrWhiteSpace(player.Class)) continue;
            var species = Species.FriendlyName(player.Class);
            counts[species] = counts.GetValueOrDefault(species) + 1;
        }

        // Build against the FULL roster, not just the species that have a configured cap —
        // UpdatePlayables is authoritative: anything we don't include is implicitly disabled.
        var desired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var species in Species.All)
        {
            var cap = limits.Caps.TryGetValue(species, out var c) ? c : SpeciesPopulationLimits.Unlimited;
            desired[species] = cap < 0 || counts.GetValueOrDefault(species) < cap;
        }

        if (_lastState is not null && StateEquals(_lastState, desired)) return;

        var playables = string.Join(",", desired.Where(kv => kv.Value).Select(kv => kv.Key));
        await rcon.ExecuteAsync(client => client.SendCommandAsync(EvrimaRconCommand.UpdatePlayables, playables));

        _lastState = desired;
    }

    private static bool StateEquals(Dictionary<string, bool> a, Dictionary<string, bool> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || other != value) return false;
        }
        return true;
    }
}
