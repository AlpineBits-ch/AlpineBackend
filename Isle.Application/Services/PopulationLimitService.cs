using IsleBridge.Sdk;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services;

/// <summary>
/// Enforces the per-species population caps in <see cref="SpeciesPopulationLimits"/>. Every minute it
/// reads the live roster over RCON, counts how many of each species are alive, and enables species
/// that are under their cap while disabling those that have hit it — so prime dinos (rex, trike, …)
/// can't exceed their configured group limit. Species capped at <see cref="SpeciesPopulationLimits.Unlimited"/>
/// stay permanently enabled.
/// </summary>
public sealed class PopulationLimitService(
    EvrimaRconClient rcon,
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
        var players = await rcon.GetPlayerData();

        // Count alive dinos per species short name (Class may be a full class path).
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in players)
        {
            if (string.IsNullOrWhiteSpace(player.Class)) continue;
            var species = Species.FriendlyName(player.Class);
            counts[species] = counts.GetValueOrDefault(species) + 1;
        }

        // Full desired state: unlimited (-1) always on; otherwise on only while under the cap.
        var desired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (species, cap) in limits.Caps)
        {
            desired[species] = cap < 0 || counts.GetValueOrDefault(species) < cap;
        }

        // Nothing changed since last tick — leave the server's allowed list as-is.
        if (_lastState is not null && StateEquals(_lastState, desired)) return;

        // so a partial update would disable every species we left out.
        await rcon.UpdatePlayables(desired);

        foreach (var (species, enabled) in desired)
        {
            if (_lastState is not null && _lastState.TryGetValue(species, out var prev) && prev == enabled)
                continue; // only log the ones that actually flipped

            logger.LogInformation("Species {Species} {State} ({Count}/{Cap})",
                species, enabled ? "enabled" : "disabled",
                counts.GetValueOrDefault(species), limits.Caps[species]);
        }

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
