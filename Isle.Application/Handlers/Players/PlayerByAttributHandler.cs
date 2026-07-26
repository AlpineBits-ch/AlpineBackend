using Isle.Contracts.Bus;
using Isle.Contracts.Dtos;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using TheIsleEvrimaRconClient.Extensions.Models;

namespace Isle.Api.Handlers.Players;

public class PlayerByAttributHandler(MicroserviceContext context)
{
    public async Task<GetPlayerBySteamIdResponse> Handle(GetPlayerBySteamIdRequest request, CancellationToken cancellationToken)
    {
        var player = await context.Players.FirstOrDefaultAsync(x => x.SteamId == request.SteamId, cancellationToken);
        if(player == null) return new GetPlayerBySteamIdResponse();
        
        return new GetPlayerBySteamIdResponse { Player = FromPlayer(player) };
    }
    
    public async Task<GetPlayerByUserIdResponse> Handle(GetPlayerByUserIdRequest request, CancellationToken cancellationToken)
    {
        var player = await context.Players.FirstOrDefaultAsync(x => x.UserId == request.UserId, cancellationToken);
        if(player == null) return new GetPlayerByUserIdResponse();

        return new GetPlayerByUserIdResponse { Player = FromPlayer(player) };
    }

    private PlayerDto FromPlayer(Player player)
    {
        return new PlayerDto()
        {
            Id = player.Id,
            CreatedAt = player.CreatedAt,
            UpdatedAt = player.UpdatedAt,
            SteamId = player.SteamId,
            Xp = player.Xp,
            UserId = player.UserId
        };
    }
}