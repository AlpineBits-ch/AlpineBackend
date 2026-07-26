using Isle.Api.Chat;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Chat;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Chat;

/// <summary>
/// Runs a player's <c>!command</c> and DMs the result back. Subscribes to
/// <see cref="ChatMessageReceivedEvent"/> rather than reading the chat feed directly, so the bridge
/// only ever has one chat connection open (see <c>ChatStreamIngestionService</c>).
/// </summary>
public class ChatCommandHandler(
    ChatCommandRegistry registry,
    IServiceScopeFactory scopeFactory,
    IBridgeClient bridgeClient,
    CommandCooldownService cooldowns,
    ILogger<ChatCommandHandler> logger)
{
    private const string Sender = "VENTA.GG";

    public async Task Handle(ChatMessageReceivedEvent @event, CancellationToken ct)
    {
        var text = @event.Text;
        if (!text.StartsWith('!'))
            return;

        var commandName = text.Split(' ')[0].TrimStart('!');
        if (!registry.TryResolve(commandName, out var commandType))
        {
            await ReplyAsync(@event.SteamId, $"Command {commandName} not found", ct);
            return;
        }

        // One scope for the message: the player lookup and the command instance share a
        // MicroserviceContext, so a command sees the player it was dispatched for.
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId, ct);
        if (player is null)
            return;

        var command = (ChatCommand)ActivatorUtilities.CreateInstance(scope.ServiceProvider, commandType);

        var commandContext = new CommandContext
        {
            PlayerSteam = @event.SteamId,
            PlayerName = @event.Name ?? string.Empty,
            Arguments = text.Split(' ').Skip(1).ToArray(),
            PlayerId = player.Id,
            IsAdmin = player.IsAdmin,
            HealthData = new DinoHealthData(),
        };

        await ApplyLiveDinoContextAsync(commandContext, @event.SteamId, ct);

        if (!command.CanRun(commandContext))
        {
            await ReplyAsync(@event.SteamId, "You are not allowed to run this command.", ct);
            return;
        }

        if (command.Cooldown > TimeSpan.Zero)
        {
            var remaining = await cooldowns.GetRemainingAsync(player.Id, command.Name, ct);
            if (remaining is { } left)
            {
                await ReplyAsync(@event.SteamId,
                    $"!{command.Name} is on cooldown, try again in {Math.Ceiling(left.TotalSeconds)}s.", ct);
                return;
            }
        }

        var response = await command.ExecuteAsync(commandContext);
        await ReplyAsync(@event.SteamId, response, ct);

        if (command.Cooldown > TimeSpan.Zero)
            await cooldowns.StartAsync(player.Id, command.Name, command.Cooldown, ct);
    }

    /// <summary>
    /// Reads the player's live dino so commands see their real species/growth/vitals. A player
    /// without a spawned pawn simply has no stats; commands handle that.
    /// </summary>
    private async Task ApplyLiveDinoContextAsync(CommandContext commandContext, string steamId, CancellationToken ct)
    {
        try
        {
            var stats = await bridgeClient.GetStatsAsync(steamId, ct);
            commandContext.PlayerSpecies = stats.Species;
            commandContext.PlayerGrowth = stats.Growth;

            if (stats.Vitals is { } vitals)
            {
                commandContext.HealthData = new DinoHealthData
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
            logger.LogDebug(ex, "Could not read live stats for {Steam}; command runs without dino context", steamId);
        }
    }

    private Task ReplyAsync(string steamId, string text, CancellationToken ct) =>
        bridgeClient.DmAsync(text: text, mode: ChatMode.Spatial, steam: steamId, sender: Sender, ct: ct);
}
