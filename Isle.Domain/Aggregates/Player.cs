using Domain;
using Isle.Domain.Entity;
using Isle.Domain.Events.Player;
using Persistence;
using Sqids;

namespace Isle.Domain.Aggregates;

public class CreatePlayerArgs
{
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; } = false;
    public string SteamId { get; set; }
}

public class Player : Aggregate<Player>, IPrefixedEntity
{
    private static readonly SqidsEncoder<int> _sqids = new(new SqidsOptions
    {
        MinLength = 6,
    });    public static string Prefix { get; } = "player";
    public virtual Storage Storage { get; set; }
    public long Xp { get; init; }
    public string SteamId { get; set; }
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; }
    public int FriendlyIdSeq { get; set; }           

    public string FriendlyId => _sqids.Encode(FriendlyIdSeq);
    
    
    public string? InGameName { get; set; }
    
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

    
 

    public void LinkUserId(string userId)
    {
        this.UserId = userId;
        this.AddDomainEvent(PlayerUserIdUnlinked.FromPlayer(this));
    }

    public void UnlinkUserId()
    {
        this.UserId = null;
        this.AddDomainEvent(PlayerUserIdUnlinked.FromPlayer(this));
    }

    public void SetAdmin()
    {
        this.IsAdmin = true;
        this.AddDomainEvent(new PlayerPromotedToAdmin()
        {
            PlayerId = this.Id
        });
    }
    
    public void UnsetAdmin()
    {
        this.IsAdmin = false;
        this.AddDomainEvent(new PlayerRemovedFromAdmin()
        {
            PlayerId = this.Id
        });
    }
}