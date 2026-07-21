using Isle.Contracts.Events.Player;
using IsleBridge.Sdk;
using Wolverine;

namespace Isle.Api.Handlers;

public class CheckPlayerConnectedToVoiceHandler
{
    public async Task<object> Handle(PlayerConnectedEvent @event, IBridgeClient client)
    {
        await client.NotifyAsync(@event.SteamId, "Welome to Venta.gg!");

        return new EnsurePlayerConnectedToVoiceEvent()
        {
            @PlayerId = @event.PlayerId,
            @SteamId = @event.SteamId       
        }.DelayedFor(TimeSpan.FromSeconds(60));
    }
    
    public async Task<object> Handle(EnsurePlayerConnectedToVoiceEvent @event, IBridgeClient client)
    {
        await client.DmAsync("Hi! Hope you are enjoying the game. Please make sure you are connected to voice. Download our client on https://venta.gg and link your steam!", @event.SteamId, "VENTA.GG");
        
        return @event.DelayedFor(TimeSpan.FromSeconds(120));
        
    }
}