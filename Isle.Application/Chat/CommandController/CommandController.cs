using Isle.Api.Chat.CommandController.Commands;
using Isle.Domain.Entity;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Chat.CommandController;

public class CommandController(IChatStream chat, ILogger<ChatWatcher> logger, IServiceProvider sp, IBridgeClient bridgeClient) : BackgroundService
{
    public static  ICollection<Type> RegisteredTypes { get; } = [typeof(DebugCommand), typeof(CreateInviteCommand)];
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
                    
                    var command = Commands.FirstOrDefault(c => c.Name == text.Split(' ')[0].Replace("!", ""));
                    if(command is null) continue;
                    
                    var response = await command.ExecuteAsync(new CommandContext()
                    {
                        PlayerSteam = msg.Steam,
                        PlayerName = msg.Name ?? string.Empty,
                        Arguments = text.Split(' ').Skip(1).ToArray(),
                        HealthData = new DinoHealthData(),
                        PlayerSpecies = "Rex of course"
                    });

                    await bridgeClient.DmAsync(text: response, steam: msg.Steam, sender: "RCON", ct: stoppingToken);
                    
                
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