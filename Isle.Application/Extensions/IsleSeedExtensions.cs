using System.Numerics;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Extensions;

public static class IsleSeedExtensions
{
    /// <summary>
    /// Seeds the built-in game mode definitions on first boot.
    ///
    /// <para>King of the Hill is still unfinished — <c>KingOfTheHillMode</c> has no behaviour yet —
    /// but the definition carries the zone and trigger config the mode will run on, so it is seeded
    /// ahead of the implementation rather than being configured by hand later.</para>
    /// </summary>
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
        });

        await dbContext.SaveChangesAsync();
    }
}
