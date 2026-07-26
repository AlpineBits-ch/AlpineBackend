using System.Numerics;
using Isle.Api.Services.Rcon;
using IsleBridge.Sdk;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Services.World;

/// <summary>Keeps <see cref="WorldRosterCache"/> fresh.</summary>
public sealed class WorldRosterService(
    IRconGateway rcon,
    WorldRosterCache cache,
    RegionMap regions,
    ILogger<WorldRosterService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SafeRefreshAsync();

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            var players = await rcon.ExecuteAsync(client => client.GetPlayerData());

            var entries = new List<RosterEntry>(players.Count);
            foreach (var player in players)
            {
                if (string.IsNullOrWhiteSpace(player.PlayerId))
                    continue;

                var position = player.Location is { } location
                    ? new Vector3((float)location.X, (float)location.Y, (float)location.Z)
                    : Vector3.Zero;

                var region = regions.Resolve(position);

                entries.Add(new RosterEntry(
                    Steam: player.PlayerId,
                    Name: player.Name ?? string.Empty,
                    Species: string.IsNullOrWhiteSpace(player.Class) ? "Unknown" : Species.FriendlyName(player.Class),
                    Growth: player.Growth,
                    Position: position,
                    RegionId: region?.Id,
                    LocationName: regions.Describe(position)));
            }

            cache.Replace(entries);
        }
        catch (Exception ex)
        {
            // Leave the previous snapshot in place; consumers gate on WorldRosterCache.IsStale.
            logger.LogWarning(ex, "World roster refresh failed");
        }
    }
}
