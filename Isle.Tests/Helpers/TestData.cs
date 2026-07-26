using System.Numerics;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;

namespace Isle.Tests.Helpers;

/// <summary>Small object-mother helpers so every test isn't re-deriving the same aggregate shape.</summary>
internal static class TestData
{
    public static GameModeDefinition KothDefinition(
        Vector3? center = null,
        float radius = 5000,
        int? minPlayersToTrigger = 1,
        TimeSpan? maxDuration = null,
        TimeSpan? cooldown = null,
        bool enabled = true,
        DateTime? lastRunAt = null,
        List<RewardConfig>? rewards = null) => new()
    {
        Id = GameModeDefinition.GenerateId(),
        DisplayName = "King of the Hill",
        Type = GameModeType.Casual,
        Enabled = enabled,
        CreatedAt = DateTime.UtcNow,
        LastRunAt = lastRunAt,
        MaxDuration = maxDuration ?? TimeSpan.FromMinutes(10),
        Cooldown = cooldown ?? TimeSpan.FromMinutes(20),
        MinParticipants = 1,
        MaxParticipants = 30,
        Zone = new GeoFenceData
        {
            Shape = GeoFenceShape.Circle,
            Center = center ?? new Vector3(1000, 1000, 0),
            Radius = radius,
        },
        Trigger = new TriggerConfig
        {
            Type = TriggerType.ZoneEntry,
            MinPlayersToTrigger = minPlayersToTrigger,
        },
        Rewards = rewards ?? [],
    };

    public static Player Player(string steamId, string? inGameName = null)
    {
        var player = Isle.Domain.Aggregates.Player.Create(new CreatePlayerArgs { SteamId = steamId });
        player.InGameName = inGameName;
        return player;
    }
}
