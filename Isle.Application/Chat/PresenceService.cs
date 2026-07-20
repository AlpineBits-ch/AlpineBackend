using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;

namespace Isle.Api.Chat;

public class PresenceService(IBridgeClient bridgeClient, ILogger<PresenceService> logger, MicroserviceContext context) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            var players = await bridgeClient.GetPlayersAsync(stoppingToken);
            foreach (var player in players.Players)
            {
                logger.LogInformation($"Player {player} is online");
                
            }
            await Task.Delay(3000, stoppingToken);
        }
        
    }
}