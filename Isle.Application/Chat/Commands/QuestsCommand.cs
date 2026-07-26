using Isle.Api.Services.Quests;
using Isle.Api.Services.World;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

/// <summary>
/// Lists what is currently running. Coordinates are included for the same reason the broadcasts carry
/// them: the region names come from a placeholder table and cannot be trusted yet.
///
/// <para>Each line leads with the quest's friendly id, which is the handle players use when they talk
/// to each other or to an admin about a specific run — the underlying ksuid is not something anyone
/// can read out loud.</para>
/// </summary>
public class QuestsCommand(MicroserviceContext db, QuestProgressLedger presence) : ChatCommand
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

        var lines = new List<string>(active.Count);

        foreach (var instance in active)
        {
            var where = instance.LocationName ?? "an unmapped area";
            var coords = RegionMap.FormatCoordinates(instance.WorldX, instance.WorldY);
            var minutes = Math.Max(0, (int)Math.Ceiling((instance.ExpiresAt - now).TotalMinutes));
            var place = string.IsNullOrEmpty(coords) ? where : $"{where} ({coords})";

            // Anyone the presence sampler has seen there, not only those who have stayed long enough to
            // qualify. Players use this to check the quest is registering them at all, and a headcount
            // that reads 0 until you have stood still for a minute answers the wrong question.
            var here = await presence.VisitorCountAsync(instance.Id);
            var crowd = here == 0 ? string.Empty : $", {here} there";

            lines.Add($"[{instance.FriendlyId}] {instance.Title} at {place} - {minutes}m left{crowd}");
        }

        return string.Join(" | ", lines);
    }

    public override string Name { get; } = "quests";
    public override string Description { get; } = "Lists the quests currently running and where they are";
    public override bool IsAdminOnly { get; set; } = false;
    public override TimeSpan Cooldown => TimeSpan.FromSeconds(15);
}
