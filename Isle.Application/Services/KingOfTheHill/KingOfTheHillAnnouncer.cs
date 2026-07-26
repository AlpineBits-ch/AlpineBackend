using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>
/// Every King of the Hill chat line players see. Mirrors <c>QuestAnnouncer</c>'s broadcast/whisper
/// contract exactly: broadcasts go out as a global chat message with no target steam id, and nothing
/// here ever throws — a dropped announcement must not roll back the match that caused it.
/// </summary>
public sealed class KingOfTheHillAnnouncer(
    IBridgeClient bridge,
    ILogger<KingOfTheHillAnnouncer> logger,
    PlayerPresenceManager presenceManager,
    MicroserviceContext context)
{
    private const string Sender = "VENTA.GG";

    public Task AnnounceStartedAsync(GameModeDefinition definition, CancellationToken ct = default)
    {
        var coords = RegionMap.FormatCoordinates(definition.Zone.Center.X, definition.Zone.Center.Y);
        return BroadcastAsync($"King of the Hill has started! Take the hill{Suffix(coords)}.", ct);
    }

    public Task AnnounceWonAsync(GameModeDefinition definition, string winnerName, CancellationToken ct = default) =>
        BroadcastAsync($"{winnerName} has taken King of the Hill!", ct);

    public Task AnnounceTimedOutAsync(GameModeDefinition definition, string? winnerName, CancellationToken ct = default) =>
        BroadcastAsync(winnerName is null
            ? "King of the Hill has ended. Nobody held the hill long enough to win."
            : $"King of the Hill has ended. {winnerName} held the hill the longest.", ct);

    public Task AnnounceCancelledAsync(CancellationToken ct = default) =>
        BroadcastAsync("King of the Hill has been called off.", ct);

    /// <summary>Global chat, everyone online. Never throws — a dropped announcement must not roll back the match that caused it.</summary>
    public async Task BroadcastAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var playerIds = presenceManager.GetAllPlayerIds();
            var steamIds = context.Players.Where(p => playerIds.Contains(p.Id)).Select(p => p.SteamId).ToArray();

            foreach (var steamId in steamIds)
            {
                var result = await bridge.DmAsync(text: message, steam: steamId, sender: Sender, mode: ChatMode.Spatial, ct: ct);
                if (!result.Ok)
                    logger.LogWarning("KOTH broadcast returned {Code}: {Message}", result.CodeRaw, result.Msg);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KOTH broadcast failed: {Message}", message);
        }
    }

    /// <summary>Direct line to one player. Same non-throwing contract as <see cref="BroadcastAsync"/>.</summary>
    public async Task WhisperAsync(string steam, string message, CancellationToken ct = default)
    {
        try
        {
            await bridge.DmAsync(text: message, steam: steam, sender: Sender, mode: ChatMode.Spatial, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KOTH whisper to {Steam} failed", steam);
        }
    }

    private static string Suffix(string coords) => string.IsNullOrEmpty(coords) ? string.Empty : $" ({coords})";
}
