using ImTools;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.Quests;

/// <summary>
/// Every quest and bounty line players see is built here. Broadcasts go out as a global chat message
/// with no target steam id, which the bridge fans out to everyone online.
///
/// <para>Place names come from <see cref="RegionMap"/> and are only as good as the region table, which
/// is currently placeholder data. Every announcement therefore also carries raw X/Y coordinates —
/// when the name is wrong or reads as "an unmapped part of the island", the numbers still point
/// somewhere real.</para>
/// </summary>
public sealed class QuestAnnouncer(IBridgeClient bridge, ILogger<QuestAnnouncer> logger, PlayerPresenceManager presenceManager, MicroserviceContext context)
{
    private const string Sender = "VENTA.GG";

    /// <summary>Announces a freshly spawned quest.</summary>
    public Task AnnounceQuestAsync(Quest quest, QuestInstance instance, CancellationToken ct = default)
    {
        var location = instance.LocationName ?? "an unmapped part of the island";
        var coords = RegionMap.FormatCoordinates(instance.WorldX, instance.WorldY);

        var body = string.IsNullOrWhiteSpace(quest.AnnouncementTemplate)
            ? $"New quest: {instance.Title} at {location}."
            : quest.AnnouncementTemplate
                .Replace("{location}", location, StringComparison.OrdinalIgnoreCase)
                .Replace("{coords}", coords, StringComparison.OrdinalIgnoreCase);

        return BroadcastAsync(Append(body, coords), ct);
    }

    /// <summary>
    /// The killing-spree call-out. Reads as the spec asked: species, place, coordinates, and the
    /// instruction that they are marked.
    /// </summary>
    public Task AnnounceBountyAsync(QuestInstance instance, CancellationToken ct = default)
    {
        var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "A dinosaur" : $"A {instance.TargetSpecies}";
        var location = instance.LocationName ?? "an unmapped part of the island";
        var coords = RegionMap.FormatCoordinates(instance.WorldX, instance.WorldY);

        var body = $"{species} is on a killing spree at {location}. They have been marked, track them and eliminate them.";
        return BroadcastAsync(Append(body, coords), ct);
    }

    public Task AnnounceBountyClaimedAsync(QuestInstance instance, string killerName, CancellationToken ct = default)
    {
        var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "the marked dinosaur" : $"the marked {instance.TargetSpecies}";
        return BroadcastAsync($"{killerName} has put {species} down. The bounty is claimed.", ct);
    }

    /// <summary>
    /// The bounty ended with the target still standing. <paramref name="participants"/> is how many
    /// hunters <i>actually received</i> a reward — not how many qualified for one. Pass the payout's own
    /// count, never the ranked count: a hunter who logged off before the clock ran out ranks but cannot
    /// be paid, and crediting them here tells the whole lobby about money nobody got. Zero on the paths
    /// where the bounty was called off rather than outlasted, which is why the credit line is conditional.
    /// </summary>
    public Task AnnounceBountyExpiredAsync(QuestInstance instance, int participants = 0, CancellationToken ct = default)
    {
        var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "The marked dinosaur" : $"The marked {instance.TargetSpecies}";

        var credit = participants switch
        {
            0 => string.Empty,
            1 => " The hunter who drew blood has been paid.",
            _ => $" The {participants} hunters who drew blood have been paid.",
        };

        return BroadcastAsync($"{species} survived the hunt. The bounty has ended.{credit}", ct);
    }

    /// <summary>
    /// The target died to something that was not a player. Deliberately does not name a killer, and
    /// deliberately does not read as a win for anyone — but it does credit the hunters, because the
    /// wounds they put in are usually why the target drowned or starved in the first place.
    ///
    /// <para><paramref name="participants"/> is how many hunters <i>actually received</i> a reward, not
    /// how many earned one. The two diverge whenever the granter finds no live pawn to write to — which
    /// on this path is common, since the hunters who wore down a target that then drowned may well be
    /// dead themselves. Zero reads as "nobody was there to claim it", which is the honest line when the
    /// payout reached no one.</para>
    /// </summary>
    public Task AnnounceBountyDiedAsync(QuestInstance instance, int participants, CancellationToken ct = default)
    {
        var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "The marked dinosaur" : $"The marked {instance.TargetSpecies}";

        var credit = participants switch
        {
            0 => "Nobody was there to claim it.",
            1 => "The hunter who wore them down has been paid.",
            _ => $"The {participants} hunters who wore them down have been paid.",
        };

        return BroadcastAsync($"{species} is dead, but not by anyone's jaws. {credit}", ct);
    }

    /// <summary>
    /// Somebody fulfilled a quest. Reads differently depending on what was asked: a hunt has one winner
    /// and naming them is the point, while an exploration is a crowd who all made the trip and the
    /// headcount is the story.
    ///
    /// <para><paramref name="paidCount"/> is how many players <i>actually received</i> a reward, never
    /// how many earned one — the same contract as the two bounty announcements, and for the same
    /// reason: a player who logged off between qualifying and resolution has no live pawn for the
    /// granter to write to, and counting them tells the whole lobby about a payout that never
    /// happened.</para>
    /// </summary>
    /// <param name="winnerName">The player to name, or null on a quest nobody won outright.</param>
    public Task AnnounceQuestCompletedAsync(
        QuestInstance instance, string? winnerName, int paidCount, CancellationToken ct = default)
    {
        var where = instance.LocationName ?? "an unmapped part of the island";

        if (!string.IsNullOrWhiteSpace(winnerName))
            return BroadcastAsync($"{winnerName} claimed {instance.Title} at {where}.", ct);

        var body = paidCount switch
        {
            // Reachable only if every single player who made the trip had logged off by the time the
            // clock ran out. Says the quest ended rather than claiming a payout that did not land.
            0 => $"Quest ended: {instance.Title} at {where}.",
            1 => $"Quest complete: one player answered the call at {where} and has been paid.",
            _ => $"Quest complete: {paidCount} players answered the call at {where} and have been paid.",
        };

        return BroadcastAsync(body, ct);
    }

    public Task AnnounceQuestExpiredAsync(QuestInstance instance, CancellationToken ct = default) =>
        BroadcastAsync($"Quest ended: {instance.Title} went unclaimed.", ct);

    /// <summary>Global chat, everyone online. Never throws — a dropped announcement must not roll back the quest that caused it.</summary>
    public async Task BroadcastAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var playerIds =  presenceManager.GetAllPlayerIds();
            var steamIds = context.Players.Where(p => playerIds.Contains(p.Id)).Select(p => p.SteamId).ToArray();

            foreach (var steamId in steamIds)
            {
                var result = await bridge.DmAsync(text: message, steam: steamId, sender: Sender, mode: ChatMode.Spatial, ct: ct);
                if (!result.Ok)
                    logger.LogWarning("Quest broadcast returned {Code}: {Message}", result.CodeRaw, result.Msg);
            }
            
          
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quest broadcast failed: {Message}", message);
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
            logger.LogWarning(ex, "Quest whisper to {Steam} failed", steam);
        }
    }

    private static string Append(string body, string coords) =>
        string.IsNullOrEmpty(coords) || body.Contains(coords, StringComparison.Ordinal)
            ? body
            : $"{body} ({coords})";
}
