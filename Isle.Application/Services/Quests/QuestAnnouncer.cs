using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.Quests;

/// <summary>Every quest and bounty line players see is built here.</summary>
public sealed class QuestAnnouncer(IBridgeClient bridge, ILogger<QuestAnnouncer> logger)
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

    /// <summary>The killing-spree call-out.</summary>
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

    public Task AnnounceBountyExpiredAsync(QuestInstance instance, CancellationToken ct = default)
    {
        var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "The marked dinosaur" : $"The marked {instance.TargetSpecies}";
        return BroadcastAsync($"{species} survived the hunt. The bounty has ended.", ct);
    }

    /// <summary>The target died to something that was not a player.</summary>
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

    public Task AnnounceQuestExpiredAsync(QuestInstance instance, CancellationToken ct = default) =>
        BroadcastAsync($"Quest ended: {instance.Title} went unclaimed.", ct);

    /// <summary>Global chat, everyone online. Never throws — a dropped announcement must not roll back the quest that caused it.</summary>
    public async Task BroadcastAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var result = await bridge.DmAsync(text: message, steam: null, sender: Sender, mode: ChatMode.Global, ct: ct);
            if (!result.Ok)
                logger.LogWarning("Quest broadcast returned {Code}: {Message}", result.CodeRaw, result.Msg);
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
