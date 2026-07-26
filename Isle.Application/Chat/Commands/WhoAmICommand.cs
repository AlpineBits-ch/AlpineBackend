using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

public class WhoAmICommand(MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var player = await microserviceContext.Players
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == context.PlayerId);

        if (player is null)
        {
            return "There was an issue reading your account (404), please report that on our discord!";
        }

        var name = string.IsNullOrWhiteSpace(player.InGameName) ? "not linked (use !link)" : player.InGameName;
        return $"Friendly id: {player.FriendlyId} | Steam id: {player.SteamId} | In-game name: {name}";
    }

    public override string Name { get; } = "id";
    public override string Description { get; } = "Shows your friendly id and steam id";
    public override bool IsAdminOnly { get; set; } = false;
}
