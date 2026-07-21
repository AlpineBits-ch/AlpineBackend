namespace Isle.Domain.Events.Player;

public class PlayerCreated : PlayerEvent
{
    public static PlayerCreated FromPlayer( Aggregates.Player player)
    {
        return new PlayerCreated
        {
            PlayerId = player.Id,
            SteamId = player.SteamId,
            UserId = player.UserId
        };
    }
}