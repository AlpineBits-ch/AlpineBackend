using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Api.Chat.CommandController.Commands;

public class CreateInviteCommand(IDistributedCache cache, IBridgeClient bridgeClient, MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        // TODO: Add cooldown and anti cheat stuff

       
        var player = await microserviceContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.SteamId == context.PlayerSteam);
        if (player == null)
        {
            return ("You are not registered, please head over to venta.gg, download the launcher and register");
        }
        
        var receiverId = context.Arguments.FirstOrDefault();
        
        if (receiverId == null)
        {
            return ("Usage: invite {player_name}");
        }

        var invite = Invite.Create(new CreateInviteParams()
        {
            SenderPlayerId = player.Id,
            ReceiverPlayerId = "",
            ServerId = "server_123",
        });

        return "Invite created!";
        
    }

    public override string Name { get; } = "invite";
    public override string Description { get; } = "Invite another player by their in game name";
    public override bool IsAdminOnly { get; set; } = false;
}