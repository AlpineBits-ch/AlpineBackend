using Isle.Api.Services;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Isle.Api.Services.Rcon;
using TheIsleEvrimaRconClient.Extensions;
using Wolverine;

namespace Isle.Api.Handlers.Voice;

public class CheckPlayerConnectedToVoiceHandler
{
    /// <summary>Reminder cadence while we wait for the player to connect to voice.</summary>
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(120);

    /// <summary>How long a player has to connect to voice before they get kicked.</summary>
    private static readonly TimeSpan VoiceGracePeriod = TimeSpan.FromMinutes(10);

    public async Task<object?> Handle(PlayerConnectedEvent @event, IBridgeClient client, MicroserviceContext context)
    {
        await client.NotifyAsync(@event.SteamId, "Welome to Venta.gg!");

        var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == @event.PlayerId);
        if(player is null)
        {
            return null;
        }
        if (player.IsAdmin)
        {
            return null;
        }
        return new EnsurePlayerConnectedToVoiceEvent()
        {
            PlayerId = @event.PlayerId,
            SteamId = @event.SteamId,
            KickAt = DateTimeOffset.UtcNow + VoiceGracePeriod
        }.DelayedFor(TimeSpan.FromSeconds(60));
    }

    public async Task<object?> Handle(EnsurePlayerConnectedToVoiceEvent @event, IBridgeClient client,
        VoicePlayerRegistry registry, IRconGateway rcon,  MicroserviceContext context)
    {
        if (registry.TryGetPlayerId(@event.SteamId, out var playerId))
        {
            if (!string.IsNullOrWhiteSpace(playerId)) return null;
        }
        var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == @event.PlayerId);
        if(player is null)
        {
            return null;
        }
        if (player.IsAdmin)
        {
            return null;
        }
        // Grace period is up and they still aren't on voice — remove them from the server.
        var now = DateTimeOffset.UtcNow;
        if (now >= @event.KickAt)
        {
            await rcon.ExecuteAsync(client => client.Kick(@event.SteamId,
                "Kicked for not connecting to voice. Download our client at https://venta.gg and link your steam."));
            return null;
        }

        await client.DmAsync("We hope you are enjoying our servers. Please make sure you are connected to voice. Download our client on https://venta.gg and link your steam!", @event.SteamId, "VENTA.GG");

        // Keep reminding on the normal cadence, but never schedule past the kick deadline so the
        // final tick lands right when the grace period ends.
        var remaining = @event.KickAt - now;
        var delay = remaining < ReminderInterval ? remaining : ReminderInterval;
        return @event.DelayedFor(delay);
    }
}
