using Domain;
using Isle.Domain.Entity;
using Isle.Domain.Events.Player;
using Persistence;

namespace Isle.Domain.Aggregates;

public class CreatePlayerArgs
{
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; } = false;
    public string SteamId { get; set; }
}

public class Player : Aggregate<Player>, IPrefixedEntity
{
    public static string Prefix { get; } = "player";
    public virtual Storage Storage { get; set; }
    public long Xp { get; init; }
    public string SteamId { get; set; }
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; }
    
    public static Player Create(CreatePlayerArgs args)
    {
        var id = GenerateId();
        var date = DateTimeOffset.UtcNow;
        var player=  new Player
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            SteamId = args.SteamId,
            UserId = args.UserId,
            IsAdmin = args.IsAdmin,
            Storage = Aggregates.Storage.Create(id)
        };
        player.AddDomainEvent(PlayerCreated.FromPlayer(player));
        return player;
    }
    
}