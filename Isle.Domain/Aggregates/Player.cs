using Domain;
using Isle.Domain.Entity;
using Persistence;

namespace Isle.Domain.Aggregates;

public class Player : Aggregate<Player>, IPrefixedEntity
{
    public static string Prefix { get; } = "player";
    public virtual Storage Storage { get; set; }
    public long Xp { get; init; }
    public string SteamId { get; set; }
    
}