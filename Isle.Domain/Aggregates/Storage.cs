using Domain;
using Isle.Domain.Entity;
using Isle.Domain.Exceptions;
using Persistence;

namespace Isle.Domain.Aggregates;

public class Storage : Aggregate<Storage>, IPrefixedEntity
{
    public int MaxSlotCount { get; set; } = 5;
    
    public virtual Player Player { get; set; }
    public virtual string PlayerId { get; set; }
    public virtual ICollection<StorageSlot> Slots { get; set; }
    public static string Prefix { get; } = "storage";
    
    
    public bool IsFull => Slots.Count >= MaxSlotCount;

    public void RemoveSlot(string slotId)
    {
        Slots.Remove(Slots.First(x => x.Id == slotId));
    }
    
    public void AddSlot(StorageSlot slot)
    {
        if(Slots.Count >= MaxSlotCount)
            throw new StorageFullException(Id, MaxSlotCount);
        Slots.Add(slot);
    }
    
    public static Storage Create(string playerId)
    {
        return new Storage
        {
            PlayerId = playerId,
            Slots = new List<StorageSlot>()
        };
    }
    
}