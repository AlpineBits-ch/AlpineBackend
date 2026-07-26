using Isle.Api.Services.Quests;
using Isle.Api.Services.World;

namespace Isle.Api.Chat.Commands;

/// <summary>Shows who is currently marked.</summary>
public class BountyCommand(BountyService bounties) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var open = await bounties.GetOpenBountiesAsync();
        if (open.Count == 0)
            return "Nobody is marked right now. Keep it that way, or don't.";

        var now = DateTimeOffset.UtcNow;

        var lines = open.Select(instance =>
        {
            var species = string.IsNullOrWhiteSpace(instance.TargetSpecies) ? "A dinosaur" : instance.TargetSpecies;
            var where = instance.LocationName ?? "an unmapped area";
            var coords = RegionMap.FormatCoordinates(instance.WorldX, instance.WorldY);
            var minutes = Math.Max(0, (int)Math.Ceiling((instance.ExpiresAt - now).TotalMinutes));
            var place = string.IsNullOrEmpty(coords) ? where : $"{where} ({coords})";
            return $"{species} last seen at {place} - {minutes}m left";
        });

        return string.Join(" | ", lines);
    }

    public override string Name { get; } = "bounty";
    public override string Description { get; } = "Shows who is marked, and where they were last seen";
    public override bool IsAdminOnly { get; set; } = false;
    public override TimeSpan Cooldown => TimeSpan.FromSeconds(15);
}
