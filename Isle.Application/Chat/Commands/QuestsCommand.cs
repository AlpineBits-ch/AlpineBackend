using Isle.Api.Services.World;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

/// <summary>
/// Lists what is currently running. Coordinates are included for the same reason the broadcasts carry
/// them: the region names come from a placeholder table and cannot be trusted yet.
/// </summary>
public class QuestsCommand(MicroserviceContext db) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var now = DateTimeOffset.UtcNow;

        var active = await db.QuestInstances
            .AsNoTracking()
            .Where(i => i.State == QuestInstanceState.Active && i.Type != QuestType.Bounty)
            .OrderBy(i => i.ExpiresAt)
            .Take(5)
            .ToListAsync();

        if (active.Count == 0)
            return "No quests are running right now. Check back shortly.";

        var lines = active.Select(instance =>
        {
            var where = instance.LocationName ?? "an unmapped area";
            var coords = RegionMap.FormatCoordinates(instance.WorldX, instance.WorldY);
            var minutes = Math.Max(0, (int)Math.Ceiling((instance.ExpiresAt - now).TotalMinutes));
            var place = string.IsNullOrEmpty(coords) ? where : $"{where} ({coords})";
            return $"{instance.Title} at {place} - {minutes}m left";
        });

        return string.Join(" | ", lines);
    }

    public override string Name { get; } = "quests";
    public override string Description { get; } = "Lists the quests currently running and where they are";
    public override bool IsAdminOnly { get; set; } = false;
    public override TimeSpan Cooldown => TimeSpan.FromSeconds(15);
}
