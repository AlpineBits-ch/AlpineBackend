namespace Isle.Domain.Events.Player;

public class PlayerUserIdUnlinked : PlayerEvent
{
    public static PlayerUserIdUnlinked FromPlayer(Aggregates.Player player) =>
        new()
        {
            PlayerId = player.Id,
            SteamId = player.SteamId
        };
}