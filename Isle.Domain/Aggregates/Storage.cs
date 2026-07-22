using Domain;
using Isle.Domain.Entity;
using Isle.Domain.Exceptions;
using Persistence;

namespace Isle.Domain.Aggregates;

public class Storage : Aggregate<Storage>, IPrefixedEntity
{
    /// <summary>Minimum growth (0..1) a dino must have reached before it can be stored or loaded.</summary>
    public const double GrowthThreshold = 0.5;

    /// <summary>XP cost of unlocking one additional storage slot.</summary>
    public const long SlotPurchaseCost = 5000;

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

    /// <summary>
    /// Captures a dino into a new storage slot. Throws <see cref="GrowthTooLowException"/> if the
    /// dino has not reached <see cref="GrowthThreshold"/>, or <see cref="StorageFullException"/>
    /// if there is no free slot.
    /// </summary>
    public StorageSlot StoreDino(CreateStorageSlotParams parameters)
    {
        if (parameters.Growth < GrowthThreshold)
            throw new GrowthTooLowException(parameters.Growth, GrowthThreshold);

        var slot = StorageSlot.Create(parameters);
        slot.StorageId = Id;
        AddSlot(slot);
        return slot;
    }

    /// <summary>Increases the slot capacity by one. Payment is handled on the player aggregate.</summary>
    public void PurchaseSlot()
    {
        MaxSlotCount += 1;
    }

    /// <summary>
    /// Marks the given slot as the one currently loaded out into the world, clearing the flag on all
    /// others (a player can only have one live dino at a time).
    /// </summary>
    public void MarkDeployed(string slotId)
    {
        foreach (var slot in Slots)
            slot.IsDeployed = slot.Id == slotId;
    }

    /// <summary>
    /// Removes every slot whose dino is currently deployed (i.e. just died), freeing that capacity.
    /// <see cref="MaxSlotCount"/> is untouched, so the freed slots can hold a new dino. Returns how
    /// many were wiped.
    /// </summary>
    public int WipeDeployed()
    {
        var deployed = Slots.Where(s => s.IsDeployed).ToList();
        foreach (var slot in deployed)
            Slots.Remove(slot);
        return deployed.Count;
    }

    public static Storage Create(string playerId)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        return new Storage
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            PlayerId = playerId,
            Slots = new List<StorageSlot>()
        };
    }
    
}