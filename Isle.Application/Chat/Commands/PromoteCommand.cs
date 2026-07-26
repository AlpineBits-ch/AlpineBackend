using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Runtime;

namespace Isle.Api.Chat.Commands;

public class PromoteCommand(MicroserviceContext microserviceContext, IMessageBus bus) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var steamId = context.Arguments.FirstOrDefault();
        if (steamId == null)
        {
            return "Usage: Promote <steamId>";
        }

        var player = microserviceContext.Players.FirstOrDefault(x => x.SteamId == steamId);
        if (player == null)
        {
            return $"Player with steamId {steamId} not found";
        }


        await bus.PublishAsync(new ChangePlayerAdminStatusCommand()
        {
            PlayerId = player.Id,
            IsAdmin = true,
        });

        return $"Player {steamId} promoted to admin";
    }

    public override string Name { get; } = "promote";
    public override string Description { get; } = "Promotes a player to admin";
    public override bool IsAdminOnly { get; set; } = true;
}