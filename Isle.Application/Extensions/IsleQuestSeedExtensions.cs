using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Extensions;

public static class IsleQuestSeedExtensions
{
    /// <summary>Seeds the starter quest templates on first boot.</summary>
    public static async Task SeedQuestsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await db.Quests.AnyAsync())
            return;

        var locations = new Dictionary<string, QuestLocation>();

        QuestLocation Location(string regionId, string title, string description)
        {
            if (locations.TryGetValue(regionId, out var existing))
                return existing;

            var location = new QuestLocation
            {
                Id = QuestLocation.GenerateId(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RegionId = regionId,
                Title = title,
                Description = description,
                GeoFence = new GeoFenceData { Shape = GeoFenceShape.Circle, Radius = 0 },
                Quests = new List<Quest>(),
            };

            locations[regionId] = location;
            return location;
        }

        var quests = new List<Quest>
        {
            BuildQuest(
                name: "Migration",
                description: "The herds are moving. Travel to the marked region and stay a while.",
                type: QuestType.Exploration,
                announcement: "The herds are moving toward {location}. Head there and stake a claim.",
                duration: TimeSpan.FromMinutes(30),
                cooldown: TimeSpan.FromMinutes(45),
                minOnline: 4,
                weight: 3,
                // A long walk across open ground: it pays for the trip out, and a little growth for
                // having made it at all.
                rewards:
                [
                    new RewardConfig { RewardType = RewardType.Xp, Amount = 1500, AppliesTo = RankRequirement.AllParticipants },
                    new RewardConfig { RewardType = RewardType.HalfDiet, Amount = 0, AppliesTo = RankRequirement.AllParticipants },
                    new RewardConfig { RewardType = RewardType.HalfWater, Amount = 0, AppliesTo = RankRequirement.AllParticipants },
                    new RewardConfig { RewardType = RewardType.GrowthBoost, Amount = 2, AppliesTo = RankRequirement.AllParticipants },
                ],
                locations:
                [
                    Location("highlands", "The Highlands", "Open ground, little cover."),
                    Location("northern_jungle", "Northern Jungle", "Dense canopy, ambush country."),
                    Location("south_plains", "South Plains", "Wide and exposed."),
                    Location("swamps", "The Swamps", "Slow going, good water."),
                ]),

            BuildQuest(
                name: "Watering Hole",
                description: "Drink and rest at the marked water. Bring friends, or don't.",
                type: QuestType.Exploration,
                announcement: "Fresh water has been sighted at {location}. Drink deep.",
                duration: TimeSpan.FromMinutes(25),
                cooldown: TimeSpan.FromMinutes(40),
                minOnline: 2,
                weight: 2,
                // The cheap, frequent one.
                rewards:
                [
                    new RewardConfig { RewardType = RewardType.FullWater, Amount = 0, AppliesTo = RankRequirement.AllParticipants },
                    new RewardConfig { RewardType = RewardType.FullStamina, Amount = 0, AppliesTo = RankRequirement.AllParticipants },
                    new RewardConfig { RewardType = RewardType.Xp, Amount = 750, AppliesTo = RankRequirement.AllParticipants },
                ],
                locations:
                [
                    Location("swamps", "The Swamps", "Slow going, good water."),
                    Location("sanctuary_east_lake", "East Lake Sanctuary", "Sheltered water."),
                    Location("sanctuary_delta", "Delta Sanctuary", "River mouth."),
                ]),

            BuildQuest(
                name: "The Hunt",
                description: "Prey has been sighted in the marked region. Go and eat.",
                type: QuestType.Hunt,
                announcement: "Prey has been sighted around {location}. Hunt well.",
                duration: TimeSpan.FromMinutes(30),
                cooldown: TimeSpan.FromMinutes(50),
                minOnline: 4,
                weight: 2,
                // Winner-takes-all and genuinely dangerous, so it pays the claimer back into a state
                // where they can keep hunting, plus growth they carry off the map.
                rewards:
                [
                    new RewardConfig { RewardType = RewardType.FullDiet, Amount = 0, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.FullHealth, Amount = 0, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.Xp, Amount = 2000, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.GrowthBoost, Amount = 3, AppliesTo = RankRequirement.Winner },
                ],
                locations:
                [
                    Location("northern_jungle", "Northern Jungle", "Dense canopy, ambush country."),
                    Location("highlands", "The Highlands", "Open ground, little cover."),
                    Location("south_plains", "South Plains", "Wide and exposed."),
                ]),

            // Required by the spree detector.
            BuildQuest(
                name: "Killing Spree",
                description: "A marked player is hunting. Put them down.",
                type: QuestType.Bounty,
                announcement: null,
                duration: BountyDuration,
                cooldown: TimeSpan.Zero,
                minOnline: 1,
                weight: 1,
                // The top of the ladder.
                rewards:
                [
                    new RewardConfig { RewardType = RewardType.Xp, Amount = 2500, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.FullDiet, Amount = 0, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.FullHealth, Amount = 0, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.GrowthBoost, Amount = 5, AppliesTo = RankRequirement.Winner },
                    new RewardConfig { RewardType = RewardType.StorageSlot, Amount = 1, AppliesTo = RankRequirement.Winner },
                ],
                locations: []),
        };

        db.Quests.AddRange(quests);
        await db.SaveChangesAsync();
    }

    private static readonly TimeSpan BountyDuration = TimeSpan.FromMinutes(20);

    private static Quest BuildQuest(
        string name,
        string description,
        QuestType type,
        string? announcement,
        TimeSpan duration,
        TimeSpan cooldown,
        int minOnline,
        int weight,
        IEnumerable<RewardConfig> rewards,
        IEnumerable<QuestLocation> locations)
    {
        var now = DateTimeOffset.UtcNow;

        var quest = new Quest
        {
            Id = Quest.GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            Name = name,
            Description = description,
            Type = type,
            Enabled = true,
            Weight = weight,
            Duration = duration,
            Cooldown = cooldown,
            MinOnlinePlayers = minOnline,
            AnnouncementTemplate = announcement,
        };

        foreach (var reward in rewards)
            quest.Rewards.Add(reward);

        foreach (var location in locations)
            quest.Locations.Add(location);

        return quest;
    }
}
