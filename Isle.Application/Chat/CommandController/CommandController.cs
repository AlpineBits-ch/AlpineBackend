using Isle.Api.Chat.CommandController.Commands;
using Isle.Api.Services;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;

namespace Isle.Api.Chat.CommandController;

public class CommandController(IChatStream chat, ILogger<ChatWatcher> logger, IServiceProvider sp, IBridgeClient bridgeClient, CommandCooldownService cooldowns) : BackgroundService
{
    public static  ICollection<Type> RegisteredTypes { get; } =
    [
        typeof(DebugCommand), typeof(LinkInGameName), typeof(PromoteCommand),
        typeof(StoreDinoCommand), typeof(LoadDinoCommand), typeof(BuySlotCommand), typeof(StorageInfoCommand),
        typeof(SendInviteCommand), typeof(AcceptInviteCommand), typeof(RejectInviteCommand),
        typeof(WhoAmICommand)
    ];

    // Maps command name -> type.
    private readonly Dictionary<string, Type> _commandTypes = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var prototypeScope = sp.CreateScope())
        {
            foreach (var type in RegisteredTypes)
            {
                // Instantiate once purely to read the command's Name for the lookup table; the
                // throwaway instance (and any scoped deps it pulled) is disposed with this scope.
                if (ActivatorUtilities.CreateInstance(prototypeScope.ServiceProvider, type) is ChatCommand prototype)
                    _commandTypes[prototype.Name] = type;
            }
        }

        var commandFetchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ChatMessage msg in chat.StreamAsync(stoppingToken))
                {
                    var text = msg.Text;
                    if(!text.StartsWith("!")) continue;

                    var commandName = text.Split(' ')[0].Replace("!", "");
                    if (!_commandTypes.TryGetValue(commandName, out var commandType)) continue;

                    using var scope = sp.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                    var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == msg.Steam, stoppingToken);
                    if(player is null) continue;

                    var command = (ChatCommand)ActivatorUtilities.CreateInstance(scope.ServiceProvider, commandType);

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
                        continue;
                    }

                    if (command.Cooldown > TimeSpan.Zero)
                    {
                        var remaining = await cooldowns.GetRemainingAsync(player.Id, command.Name, stoppingToken);
                        if (remaining is { } left)
                        {
                            await bridgeClient.DmAsync(text: $"!{command.Name} is on cooldown, try again in {Math.Ceiling(left.TotalSeconds)}s.", mode: ChatMode.Spatial, steam: msg.Steam, sender: "VENTA.GG", ct: stoppingToken);
                            continue;
                        }
                    }

                    var response = await command.ExecuteAsync(commandContext);

                    await bridgeClient.DmAsync(text: response, mode: ChatMode.Spatial, steam: msg.Steam, sender: "VENTA.GG", ct: stoppingToken);

                    if (command.Cooldown > TimeSpan.Zero)
                        await cooldowns.StartAsync(player.Id, command.Name, command.Cooldown, stoppingToken);
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
