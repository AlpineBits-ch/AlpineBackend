using Isle.Domain.Aggregates;
using Isle.Domain.ValueObjects;
using Persistence;

namespace Isle.Domain.Entity;

public class QuestLocation : BaseEntity<QuestLocation>, IPrefixedEntity
{
    public string Title { get; set; }
    public string Description { get; set; }

    public static string Prefix { get; }
    
    public GeoFenceData GeoFence { get; set; }
    
    public virtual ICollection<Quest> Quests { get; set; }
}