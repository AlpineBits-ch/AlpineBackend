using Isle.Api.Services.Hosted;
using Isle.Api.Services.Rcon;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services;

/// <summary>
/// The actual world sweep over RCON: wipe all corpses and reset AI by toggling it off and back on.
/// </summary>
public sealed class WorldCleaner(IRconGateway rcon, ILogger<WorldCleaner> logger)
{
    public async Task WipeAsync()
    {
        await rcon.ExecuteAsync(client => client.WipeCorpses());

        // Clearing AI is a toggle: switch it off, then back on.
        await rcon.ExecuteAsync(client => client.ToggleAI(false));
        await rcon.ExecuteAsync(client => client.ToggleAI(true));

        logger.LogInformation("World cleanup complete: corpses wiped, AI reset");
    }
}
