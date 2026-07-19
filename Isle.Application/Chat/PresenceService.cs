using IsleBridge.Sdk;

namespace Isle.Api.Chat;

public class PresenceService(IBridgeClient bridgeClient, ILogger<PresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Checking for online players");
            var players = await bridgeClient.GetPlayersAsync(stoppingToken);
            foreach (var player in players.Players)
            {
                logger.LogInformation($"Player {player} is online");
            }
            await Task.Delay(1000, stoppingToken);
        }
        
    }
}