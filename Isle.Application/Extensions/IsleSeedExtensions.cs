using System.Numerics;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Extensions;

public static class IsleSeedExtensions
{
    /// <summary>Seeds the built-in game mode definitions on first boot.</summary>
    public static async Task SeedGameModeDefinitionsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await dbContext.GameModeDefinitions.AnyAsync())
            return;

        var now = DateTime.UtcNow;

        dbContext.GameModeDefinitions.Add(new GameModeDefinition
        {
            Id = GameModeDefinition.GenerateId(),
            DisplayName = "King of the Hill",
            Type = GameModeType.Casual,
            CreatedAt = now,
            UpdatedAt = now,
            MaxDuration = TimeSpan.FromMinutes(10),
            MinParticipants = 1,
            MaxParticipants = 30,
            Cooldown = TimeSpan.FromMinutes(20),
            Enabled = true,
            Zone = new GeoFenceData
            {
                Shape = GeoFenceShape.Circle,
                Radius = 5000,
                Center = new Vector3
                {
                    X = 333285.638f,
                    Y = -331208.952f,
                    Z = 22197.846f,
                },
            },
            Trigger = new TriggerConfig
            {
                MinPlayersToTrigger = 1,
                Type = TriggerType.ZoneEntry,
            },
            Rewards =
            [
                // Winner: also collects every Top3 and AllParticipants row below.
                new RewardConfig { RewardType = RewardType.Xp, Amount = 5000, AppliesTo = RankRequirement.Winner },
                new RewardConfig { RewardType = RewardType.FullHealth, AppliesTo = RankRequirement.Winner },
                new RewardConfig { RewardType = RewardType.FullStamina, AppliesTo = RankRequirement.Winner },
                new RewardConfig { RewardType = RewardType.GrowthBoost, Amount = 5, AppliesTo = RankRequirement.Winner },

                // Runner-up tier: second and third place by control ticks.
                new RewardConfig { RewardType = RewardType.Xp, Amount = 2000, AppliesTo = RankRequirement.Top3 },
                new RewardConfig { RewardType = RewardType.HalfDiet, AppliesTo = RankRequirement.Top3 },

                // Everyone who registered at least one control tick.
                new RewardConfig { RewardType = RewardType.Xp, Amount = 500, AppliesTo = RankRequirement.AllParticipants },
            ],
        });

        await dbContext.SaveChangesAsync();
    }
}
