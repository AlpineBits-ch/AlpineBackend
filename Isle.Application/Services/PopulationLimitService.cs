using IsleBridge.Sdk;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services;

/// <summary>
/// Enforces the per-species population caps in <see cref="SpeciesPopulationLimits"/>.
/// </summary>
public sealed class PopulationLimitService(
    EvrimaRconClient rcon,
    SpeciesPopulationLimits limits,
    ILogger<PopulationLimitService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    // Last state we pushed per species, so we only send RCON toggles when something actually changes.
    private readonly Dictionary<string, bool> _lastEnabled = new(StringComparer.OrdinalIgnoreCase);

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

        // Desired enabled state: unlimited (-1) always on; otherwise on only while under the cap.
        var changed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (species, cap) in limits.Caps)
        {
            var enabled = cap < 0 || counts.GetValueOrDefault(species) < cap;

            if (_lastEnabled.TryGetValue(species, out var previous) && previous == enabled)
                continue; // no change since last tick — skip

            changed[species] = enabled;
        }

        if (changed.Count == 0) return;

        await rcon.UpdatePlayables(changed);

        foreach (var (species, enabled) in changed)
        {
            _lastEnabled[species] = enabled;
            logger.LogInformation("Species {Species} {State} ({Count}/{Cap})",
                species, enabled ? "enabled" : "disabled",
                counts.GetValueOrDefault(species), limits.Caps[species]);
        }
    }
}
