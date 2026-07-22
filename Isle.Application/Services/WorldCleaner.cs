using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services;

/// <summary>
/// The actual world sweep over RCON: wipe all corpses and reset AI by toggling it off and back on.
/// Shared by the hourly <see cref="WorldCleanupService"/> and the admin <c>!wipeworld</c> command.
/// </summary>
public sealed class WorldCleaner(EvrimaRconClient rcon, ILogger<WorldCleaner> logger)
{
    public async Task WipeAsync()
    {
        await rcon.WipeCorpses();

        // Clearing AI is a toggle: switch it off, then back on.
        await rcon.ToggleAI(false);
        await rcon.ToggleAI(true);

        logger.LogInformation("World cleanup complete: corpses wiped, AI reset");
    }
}
