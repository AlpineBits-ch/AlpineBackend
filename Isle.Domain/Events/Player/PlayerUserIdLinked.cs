namespace Isle.Domain.Events.Player;

public class PlayerUserIdLinked : PlayerEvent
{
    public static PlayerUserIdLinked FromPlayer(Aggregates.Player player) =>
        new()
        {
            Id = player.Id,
            SteamId = player.SteamId
        };
    
}