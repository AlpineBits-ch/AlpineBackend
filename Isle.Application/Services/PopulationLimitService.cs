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
        logger.LogInformation(
            "PopulationLimitService starting. Interval={IntervalSeconds}s, roster={RosterCount} species, {CapCount} configured cap(s): {Caps}",
            Interval.TotalSeconds,
            Species.All.Count,
            limits.Caps.Count,
            FormatCaps());

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

        logger.LogInformation("PopulationLimitService stopping (cancellation requested).");
    }

    private async Task SafeEnforceAsync()
    {
        try
        {
            await EnforceAsync();
        }
        catch (Exception ex)
        {
            // A throw here (e.g. RCON "not connected") means the desired state was NOT applied and
            // _lastState is left untouched, so the next tick retries from scratch. Surface it loudly:
            // a silently failing enforce tick is exactly how every species can appear stuck disabled.
            logger.LogError(ex, "Population limit enforcement tick failed; playable state was NOT updated this tick");
        }
    }

    private async Task EnforceAsync()
    {
        logger.LogDebug("Enforce tick starting. RCON connected={Connected}", rcon.IsConnected);

        if (!rcon.IsConnected)
        {
            // GetPlayerData/UpdatePlayables both require a live connection. If we're not connected,
            // GetPlayerData typically returns an empty roster (→ we'd compute "enable everything")
            // and UpdatePlayables then throws — so nothing actually reaches the server. Flag it.
            logger.LogWarning("RCON client is not connected; this tick cannot read the roster or push playable state.");
        }

        var players = await rcon.GetPlayerData();
        logger.LogDebug("GetPlayerData returned {PlayerCount} player(s).", players.Count);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unclassified = 0;
        foreach (var player in players)
        {
            if (string.IsNullOrWhiteSpace(player.Class))
            {
                unclassified++;
                logger.LogTrace("Player {Player} ({PlayerId}) has no class (not spawned in?); excluded from counts.",
                    player.Name, player.PlayerId);
                continue;
            }

            var species = Species.FriendlyName(player.Class);
            if (!Species.IsKnown(species))
            {
                // FriendlyName falls back to the raw class when it can't map it. That raw string
                // won't match any capped species, so it is silently uncapped — worth flagging.
                logger.LogWarning(
                    "Player {Player} class {RawClass} did not resolve to a known species (got {Species}); it will not count against any cap.",
                    player.Name, player.Class, species);
            }

            counts[species] = counts.GetValueOrDefault(species) + 1;
            logger.LogTrace("Counted player {Player}: class {RawClass} -> species {Species}.",
                player.Name, player.Class, species);
        }

        logger.LogInformation(
            "Roster resolved: {PlayerCount} player(s), {UnclassifiedCount} unclassified, {DistinctSpecies} distinct species alive: {Counts}",
            players.Count, unclassified, counts.Count,
            counts.Count == 0 ? "(none)" : string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

        // Build against the FULL roster, not just the species that have a configured cap —
        // UpdatePlayables is authoritative: anything we don't include is implicitly disabled.
        var desired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var atCap = new List<string>();
        foreach (var species in Species.All)
        {
            var cap = limits.Caps.TryGetValue(species, out var c) ? c : SpeciesPopulationLimits.Unlimited;
            var count = counts.GetValueOrDefault(species);
            var enabled = cap < 0 || count < cap;
            desired[species] = enabled;
            if (!enabled) atCap.Add($"{species} ({count}/{cap})");
        }

        var enabledCount = desired.Count(kv => kv.Value);
        logger.LogInformation(
            "Desired playable state computed: {EnabledCount}/{Total} enabled, {DisabledCount} disabled{AtCap}.",
            enabledCount, desired.Count, desired.Count - enabledCount,
            atCap.Count == 0 ? " (none at cap)" : $". At cap: {string.Join(", ", atCap)}");

        if (enabledCount == 0)
        {
            // Should be impossible with an empty roster (0 < any positive cap, and Unlimited is always
            // enabled). If we ever compute this, the cap table or roster resolution is broken.
            logger.LogWarning(
                "Computed desired state disables EVERY species ({Total} total). Live counts: {Counts}. This is almost certainly a bug in cap resolution.",
                desired.Count,
                counts.Count == 0 ? "(none)" : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        if (_lastState is not null && StateEquals(_lastState, desired))
        {
            logger.LogDebug("Desired state unchanged since last successful tick; skipping UpdatePlayables RCON call.");
            return;
        }

        // Build the exact argument string the server will receive so we can see precisely what was
        // sent (format: "class:enabled,class:disabled,..."). Using the string overload guarantees the
        // logged argument is byte-for-byte what goes over the wire.
        var argument = string.Join(",",
            desired.Select(kv => $"{kv.Key}:{(kv.Value ? "enabled" : "disabled")}"));
        logger.LogInformation("Sending UpdatePlayables ({Length} chars): {Argument}", argument.Length, argument);

        var response = await rcon.UpdatePlayables(argument);
        logger.LogInformation("UpdatePlayables RCON response: {Response}",
            string.IsNullOrWhiteSpace(response) ? "(empty/no response)" : response.Trim());

        foreach (var (species, enabled) in desired)
        {
            if (_lastState is not null && _lastState.TryGetValue(species, out var prev) && prev == enabled)
                continue;

            var cap = limits.Caps.TryGetValue(species, out var c) ? c : SpeciesPopulationLimits.Unlimited;
            logger.LogInformation("Species {Species} {State} ({Count}/{Cap})",
                species, enabled ? "enabled" : "disabled",
                counts.GetValueOrDefault(species), cap);
        }

        _lastState = desired;
        logger.LogDebug("Enforce tick complete; cached new state ({EnabledCount}/{Total} enabled).",
            enabledCount, desired.Count);
    }

    private string FormatCaps()
    {
        if (limits.Caps.Count == 0) return "(none)";
        return string.Join(", ", limits.Caps
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={(kv.Value < 0 ? "unlimited" : kv.Value.ToString())}"));
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
