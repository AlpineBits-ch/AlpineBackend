using Domain;
using Isle.Domain.Entity;
using Persistence;

namespace Isle.Domain.Aggregates;

public class Storage : Aggregate<Storage>, IPrefixedEntity
{
    public int UnlockedSlotCount { get; set; }
    
    public virtual Player Player { get; set; }
    public virtual string PlayerId { get; set; }
    public virtual ICollection<StorageSlot> Slots { get; set; }
    public static string Prefix { get; } = "stor";
    
}