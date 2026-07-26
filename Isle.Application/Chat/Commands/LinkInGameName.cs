using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

public class LinkInGameName(MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PlayerName))
        {
            return "There was an issue linking your in-game name (400), please report that on our discord!";
        }
        
        var player = await microserviceContext.Players.FirstOrDefaultAsync(p => p.Id == context.PlayerId);
        if (player == null)
        {
            return "There was an issue linking your in-game name (404), please report that on our discord!";
        }
        
        player.InGameName = context.PlayerName;
        await microserviceContext.SaveChangesAsync();
        return $"Your in-game name has been linked! Friendly id: {player.FriendlyId}, in-game name: {player.InGameName}";
    }

    public override string Name { get; } = "link";
    public override string Description { get; } = "Links your in-game name to your account";
    public override bool IsAdminOnly { get; set; }
}