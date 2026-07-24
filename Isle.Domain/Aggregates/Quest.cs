using Domain;
using Isle.Domain.Entity;
using Isle.Domain.ValueObjects;
using Persistence;

namespace Isle.Domain.Aggregates;

public class Quest : Aggregate<Quest>, IPrefixedEntity
{
    public string Name { get; set; }
    public string Description { get; set; }
    public static string Prefix { get; } = "quest";
    
    public ICollection<RewardConfig> Rewards { get; } = new List<RewardConfig>();
    
    
    public ICollection<QuestLocation> Locations { get; } = new List<QuestLocation>();
    
}