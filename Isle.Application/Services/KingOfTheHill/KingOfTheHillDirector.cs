using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>Decides whether King of the Hill should start a match right now.</summary>
public sealed class KingOfTheHillDirector(
    MicroserviceContext context,
    WorldRosterCache roster,
    ILogger<KingOfTheHillDirector> logger)
{
    /// <summary>
    /// The seeded definition is matched by name rather than a dedicated "which mode" column — nothing in
    /// <c>GameModeDefinition</c> distinguishes a King of the Hill row from a future mode's row, and
    /// adding that column is a migration this feature deliberately avoids. If a second mode is added,
    /// give it its own, differently-named definition and this filter keeps them apart without a schema
    /// change.
    /// </summary>
    private const string DefinitionDisplayName = "King of the Hill";

    /// <summary>Roster older than this means we do not know who is in the zone, so triggering would be a guess.</summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromMinutes(2);

    /// <summary>Picks the definition to start against, or null when nothing should start right now.</summary>
    public async Task<GameModeDefinition?> ChooseAsync(CancellationToken ct = default)
    {
        if (roster.IsStale(MaxRosterAge))
        {
            logger.LogDebug("Skipping KOTH start: roster snapshot is stale");
            return null;
        }

        var definition = await FindDefinitionAsync(ct);
        if (definition is null || IsOnCooldown(definition))
            return null;

        var inZone = roster.Entries.Count(e => definition.Zone.Contains(e.Position));
        var minPlayers = definition.Trigger.MinPlayersToTrigger ?? 1;

        // TEMP: no positive-path logging existed here at all, so a silent "not enough players"
        // was indistinguishable from a bad roster read. Remove once KOTH triggering is confirmed working.
        logger.LogInformation(
            "KOTH check: {InZone}/{Min} needed, {Total} total roster entries: {Entries}",
            inZone, minPlayers, roster.Entries.Count,
            string.Join(", ", roster.Entries.Select(e =>
                $"{e.Steam}@({e.Position.X:F0},{e.Position.Y:F0},{e.Position.Z:F0})[{(definition.Zone.Contains(e.Position) ? "in" : "out")}]")));

        return inZone >= minPlayers ? definition : null;
    }

    /// <summary>Force-start path for <c>!kothadmin start</c>: bypasses the trigger gate, same as <c>!questadmin spawn</c>. Cooldown still applies.</summary>
    public async Task<GameModeDefinition?> ChooseForcedAsync(CancellationToken ct = default)
    {
        var definition = await FindDefinitionAsync(ct);
        return definition is not null && !IsOnCooldown(definition) ? definition : null;
    }

    private Task<GameModeDefinition?> FindDefinitionAsync(CancellationToken ct) =>
        context.GameModeDefinitions
            .Where(d => d.Enabled && d.DisplayName == DefinitionDisplayName)
            .FirstOrDefaultAsync(ct);

    private static bool IsOnCooldown(GameModeDefinition definition) =>
        definition.LastRunAt is { } lastRun && DateTime.UtcNow - lastRun < definition.Cooldown;
}
