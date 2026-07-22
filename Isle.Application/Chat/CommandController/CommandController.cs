using Isle.Api.Chat.CommandController.Commands;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Chat.CommandController;

public class CommandController(IChatStream chat, ILogger<ChatWatcher> logger, IServiceProvider sp, IBridgeClient bridgeClient) : BackgroundService
{
    public static  ICollection<Type> RegisteredTypes { get; } = [typeof(DebugCommand), typeof(LinkInGameName), typeof(CreateInviteCommand), typeof(PromoteCommand), typeof(StoreDinoCommand), typeof(LoadDinoCommand), typeof(BuySlotCommand), typeof(StorageInfoCommand)];
    private ICollection<ChatCommand> Commands { get; } = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        foreach (var type in RegisteredTypes)
        {
            var instance = ActivatorUtilities.CreateInstance(sp, type) as ChatCommand; 
            if(instance is null) continue;
            Commands.Add(instance);
        }
        
        var commandFetchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ChatMessage msg in chat.StreamAsync(stoppingToken))
                {
                    var text = msg.Text;
                    if(!text.StartsWith("!")) continue;

                    using var scope = sp.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                    var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == msg.Steam, stoppingToken);
                    if(player is null) continue;
                    
                    
                    
                    var command = Commands.FirstOrDefault(c => c.Name == text.Split(' ')[0].Replace("!", ""));
                    if(command is null) continue;

                    // Read the player's live dino so commands see their real species/growth/vitals.
                    string? species = null;
                    double growth = 0;
                    var healthData = new DinoHealthData();
                    try
                    {
                        var stats = await bridgeClient.GetStatsAsync(msg.Steam, stoppingToken);
                        species = stats.Species;
                        growth = stats.Growth;
                        if (stats.Vitals is { } vitals)
                        {
                            healthData = new DinoHealthData
                            {
                                Health = (long)vitals.Hp,
                                Hunger = (long)vitals.Hunger,
                                Thirst = (long)vitals.Thirst,
                                Stamina = (long)vitals.Stamina,
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not read live stats for {Steam}; command runs without dino context", msg.Steam);
                    }

                    var commandContext = new CommandContext()
                    {
                        PlayerSteam = msg.Steam,
                        PlayerName = msg.Name ?? string.Empty,
                        Arguments = text.Split(' ').Skip(1).ToArray(),
                        HealthData = healthData,
                        PlayerId = player.Id,
                        IsAdmin = player.IsAdmin,
                        PlayerSpecies = species,
                        PlayerGrowth = growth,
                    };

                    if (!command.CanRun(commandContext))
                    {
                        await bridgeClient.DmAsync(text: "You are not allowed to run this command.", mode: ChatMode.Spatial, steam: msg.Steam, sender: "VENTA.GG", ct: stoppingToken);

                        return ;
                    }
                    
                    
                    var response = await command.ExecuteAsync(commandContext);

                    await bridgeClient.DmAsync(text: response, mode: ChatMode.Spatial, steam: msg.Steam, sender: "VENTA.GG", ct: stoppingToken);


                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred in chat stream");
            }
        }, stoppingToken);

        await Task.WhenAll(commandFetchTask);
    }
}